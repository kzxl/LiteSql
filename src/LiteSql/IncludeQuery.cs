using Dapper;
using LiteSql.ChangeTracking;
using LiteSql.Mapping;
using LiteSql.Sql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace LiteSql
{
    /// <summary>
    /// Fluent query builder that allows selective FK loading via Include().
    /// When Include() is used, ONLY specified FKs are loaded (not all).
    /// Usage: db.Table.Include(x => x.Nav1).Include(x => x.Nav2).Where(...)
    /// </summary>
    public class IncludeQuery<T> where T : class
    {
        private readonly Table<T> _table;
        private readonly LiteContext _context;
        private readonly ChangeTracker _changeTracker;
        private readonly List<PropertyInfo> _includes = new List<PropertyInfo>();

        internal IncludeQuery(Table<T> table, LiteContext context, ChangeTracker changeTracker)
        {
            _table = table;
            _context = context;
            _changeTracker = changeTracker;
        }

        /// <summary>
        /// Specifies an additional FK navigation property to eagerly load.
        /// </summary>
        public IncludeQuery<T> Include(Expression<Func<T, object>> expression)
        {
            var prop = ExtractProperty(expression);
            if (prop != null && !_includes.Contains(prop))
                _includes.Add(prop);
            return this;
        }

        /// <summary>
        /// Filters entities with a predicate, loading only specified FK associations.
        /// </summary>
        public List<T> Where(Expression<Func<T, bool>> predicate)
        {
            var mapping = MappingCache.GetMapping<T>();
            var builder = new WhereBuilder(mapping);
            var (whereSql, parameters) = builder.Build(predicate);
            var fullSql = $"{SqlGenerator.GenerateSelectAll(mapping)} WHERE {whereSql}";

            _context.EnsureConnectionOpen();
            var dp = ToDp(parameters);
            var results = _context.Connection.Query<T>(fullSql, dp,
                transaction: _context.Transaction, commandTimeout: _context.CommandTimeout).ToList();
            TrackResults(results, mapping);
            LoadIncludedAssociations(results, mapping);
            return results;
        }

        /// <summary>
        /// Async Where with selective FK loading.
        /// </summary>
        public async Task<List<T>> WhereAsync(Expression<Func<T, bool>> predicate,
            CancellationToken ct = default)
        {
            var mapping = MappingCache.GetMapping<T>();
            var builder = new WhereBuilder(mapping);
            var (whereSql, parameters) = builder.Build(predicate);
            var fullSql = $"{SqlGenerator.GenerateSelectAll(mapping)} WHERE {whereSql}";

            await _context.EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            var dp = ToDp(parameters);
            var results = (await _context.Connection.QueryAsync<T>(
                new CommandDefinition(fullSql, dp, transaction: _context.Transaction,
                    commandTimeout: _context.CommandTimeout, cancellationToken: ct))
                .ConfigureAwait(false)).ToList();
            TrackResults(results, mapping);
            await LoadIncludedAssociationsAsync(results, mapping, ct).ConfigureAwait(false);
            return results;
        }

        /// <summary>
        /// Returns first matching entity with selective FK loading.
        /// </summary>
        public T FirstOrDefault(Expression<Func<T, bool>> predicate)
        {
            var mapping = MappingCache.GetMapping<T>();
            var builder = new WhereBuilder(mapping);
            var (whereSql, parameters) = builder.Build(predicate);
            var fullSql = $"{SqlGenerator.GenerateSelectAll(mapping)} WHERE {whereSql}";

            var isSqlite = _context.Connection.GetType().Name
                .IndexOf("sqlite", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isSqlite) fullSql += " LIMIT 1";
            else fullSql = fullSql.Replace("SELECT ", "SELECT TOP 1 ");

            _context.EnsureConnectionOpen();
            var dp = ToDp(parameters);
            var entity = _context.Connection.QueryFirstOrDefault<T>(fullSql, dp,
                transaction: _context.Transaction, commandTimeout: _context.CommandTimeout);
            if (entity != null)
            {
                TrackSingle(entity, mapping);
                LoadIncludedAssociations(new List<T> { entity }, mapping);
            }
            return entity;
        }

        /// <summary>
        /// Async FirstOrDefault with selective FK loading.
        /// </summary>
        public async Task<T> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate,
            CancellationToken ct = default)
        {
            var mapping = MappingCache.GetMapping<T>();
            var builder = new WhereBuilder(mapping);
            var (whereSql, parameters) = builder.Build(predicate);
            var fullSql = $"{SqlGenerator.GenerateSelectAll(mapping)} WHERE {whereSql}";

            var isSqlite = _context.Connection.GetType().Name
                .IndexOf("sqlite", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isSqlite) fullSql += " LIMIT 1";
            else fullSql = fullSql.Replace("SELECT ", "SELECT TOP 1 ");

            await _context.EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            var dp = ToDp(parameters);
            var entity = await _context.Connection.QueryFirstOrDefaultAsync<T>(fullSql, dp,
                transaction: _context.Transaction, commandTimeout: _context.CommandTimeout)
                .ConfigureAwait(false);
            if (entity != null)
            {
                TrackSingle(entity, mapping);
                await LoadIncludedAssociationsAsync(new List<T> { entity }, mapping, ct)
                    .ConfigureAwait(false);
            }
            return entity;
        }

        #region Private Helpers

        private void LoadIncludedAssociations(List<T> entities, EntityMapping mapping)
        {
            if (entities.Count == 0 || _includes.Count == 0) return;
            if (mapping.Associations == null || mapping.Associations.Count == 0) return;

            foreach (var navProp in _includes)
            {
                var assoc = mapping.Associations
                    .FirstOrDefault(a => a.IsForeignKey && a.Property.Name == navProp.Name);
                if (assoc == null) continue;

                LoadFkAssociation(entities, assoc);
            }
        }

        private async Task LoadIncludedAssociationsAsync(List<T> entities, EntityMapping mapping,
            CancellationToken ct)
        {
            if (entities.Count == 0 || _includes.Count == 0) return;
            if (mapping.Associations == null || mapping.Associations.Count == 0) return;

            foreach (var navProp in _includes)
            {
                var assoc = mapping.Associations
                    .FirstOrDefault(a => a.IsForeignKey && a.Property.Name == navProp.Name);
                if (assoc == null) continue;

                await LoadFkAssociationAsync(entities, assoc, ct).ConfigureAwait(false);
            }
        }

        private void LoadFkAssociation(List<T> entities, AssociationMapping assoc)
        {
            var fkProp = typeof(T).GetProperty(assoc.ThisKey);
            if (fkProp == null) return;

            var fkValues = entities
                .Select(e => fkProp.GetValue(e))
                .Where(v => v != null && !v.Equals(GetDefault(fkProp.PropertyType)))
                .Distinct().ToList();
            if (fkValues.Count == 0) return;

            var relatedMapping = MappingCache.GetMapping(assoc.OtherType);
            var quotedTable = SqlGenerator.QuoteTableName(relatedMapping.TableName);

            var dp = new DynamicParameters();
            var paramNames = new List<string>();
            for (int i = 0; i < fkValues.Count; i++)
            {
                paramNames.Add($"@fk{i}");
                dp.Add($"@fk{i}", fkValues[i]);
            }

            var sql = $"SELECT * FROM {quotedTable} WHERE [{assoc.OtherKey}] IN ({string.Join(", ", paramNames)})";
            _context.EnsureConnectionOpen();
            var related = _context.Connection.Query(assoc.OtherType, sql, dp,
                transaction: _context.Transaction, commandTimeout: _context.CommandTimeout).ToList();

            var otherKeyProp = assoc.OtherType.GetProperty(assoc.OtherKey);
            if (otherKeyProp == null) return;

            var lookup = new Dictionary<object, object>();
            foreach (var r in related)
            {
                var k = otherKeyProp.GetValue(r);
                if (k != null) lookup[k] = r;
            }

            foreach (var entity in entities)
            {
                var fk = fkProp.GetValue(entity);
                if (fk != null && lookup.TryGetValue(fk, out var rel))
                    assoc.Property.SetValue(entity, rel);
            }
        }

        private async Task LoadFkAssociationAsync(List<T> entities, AssociationMapping assoc,
            CancellationToken ct)
        {
            var fkProp = typeof(T).GetProperty(assoc.ThisKey);
            if (fkProp == null) return;

            var fkValues = entities
                .Select(e => fkProp.GetValue(e))
                .Where(v => v != null && !v.Equals(GetDefault(fkProp.PropertyType)))
                .Distinct().ToList();
            if (fkValues.Count == 0) return;

            var relatedMapping = MappingCache.GetMapping(assoc.OtherType);
            var quotedTable = SqlGenerator.QuoteTableName(relatedMapping.TableName);

            var dp = new DynamicParameters();
            var paramNames = new List<string>();
            for (int i = 0; i < fkValues.Count; i++)
            {
                paramNames.Add($"@fk{i}");
                dp.Add($"@fk{i}", fkValues[i]);
            }

            var sql = $"SELECT * FROM {quotedTable} WHERE [{assoc.OtherKey}] IN ({string.Join(", ", paramNames)})";
            await _context.EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            var related = (await _context.Connection.QueryAsync(assoc.OtherType, sql, dp,
                transaction: _context.Transaction, commandTimeout: _context.CommandTimeout)
                .ConfigureAwait(false)).ToList();

            var otherKeyProp = assoc.OtherType.GetProperty(assoc.OtherKey);
            if (otherKeyProp == null) return;

            var lookup = new Dictionary<object, object>();
            foreach (var r in related)
            {
                var k = otherKeyProp.GetValue(r);
                if (k != null) lookup[k] = r;
            }

            foreach (var entity in entities)
            {
                var fk = fkProp.GetValue(entity);
                if (fk != null && lookup.TryGetValue(fk, out var rel))
                    assoc.Property.SetValue(entity, rel);
            }
        }

        private void TrackResults(List<T> results, EntityMapping mapping)
        {
            if (!_context.ObjectTrackingEnabled) return;
            foreach (var entity in results)
                _changeTracker.TrackLoaded(entity, mapping);
        }

        private void TrackSingle(T entity, EntityMapping mapping)
        {
            if (!_context.ObjectTrackingEnabled) return;
            _changeTracker.TrackLoaded(entity, mapping);
        }

        private static DynamicParameters ToDp(IDictionary<string, object> parameters)
        {
            var dp = new DynamicParameters();
            if (parameters != null)
                foreach (var kv in parameters) dp.Add(kv.Key, kv.Value);
            return dp;
        }

        private static object GetDefault(Type type)
            => type.IsValueType ? Activator.CreateInstance(type) : null;

        private static PropertyInfo ExtractProperty(Expression<Func<T, object>> expression)
        {
            var body = expression.Body;
            if (body is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
                body = unary.Operand;
            return body is MemberExpression member && member.Member is PropertyInfo prop ? prop : null;
        }

        #endregion
    }
}
