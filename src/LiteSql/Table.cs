using Dapper;
using LiteSql.ChangeTracking;
using LiteSql.Mapping;
using LiteSql.Sql;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace LiteSql
{
    /// <summary>
    /// Represents a database table for entity type T.
    /// Compatible with System.Data.Linq.Table&lt;T&gt;.
    /// Provides sync + async CRUD and querying.
    /// </summary>
    public class Table<T> : IEnumerable<T> where T : class
    {
        private readonly LiteContext _context;
        private readonly ChangeTracker _changeTracker;
        private bool _noTracking;

        internal Table(LiteContext context, ChangeTracker changeTracker)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _changeTracker = changeTracker ?? throw new ArgumentNullException(nameof(changeTracker));
        }

        #region Insert / Delete / Attach

        public void InsertOnSubmit(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            _changeTracker.TrackInsert(entity);
        }

        public void InsertAllOnSubmit(IEnumerable<T> entities)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));
            foreach (var e in entities) InsertOnSubmit(e);
        }

        public void DeleteOnSubmit(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            _changeTracker.TrackDelete(entity);
        }

        public void DeleteAllOnSubmit(IEnumerable<T> entities)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));
            foreach (var e in entities) DeleteOnSubmit(e);
        }

        /// <summary>
        /// Attaches an entity for update tracking.
        /// </summary>
        public void Attach(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            _changeTracker.TrackLoaded(entity, MappingCache.GetMapping<T>());
        }

        /// <summary>
        /// Attaches with original baseline for change detection.
        /// </summary>
        public void Attach(T entity, T original)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (original == null) throw new ArgumentNullException(nameof(original));
            _changeTracker.TrackLoadedWithOriginal(entity, original, MappingCache.GetMapping<T>());
        }

        /// <summary>
        /// Attaches and optionally marks as modified.
        /// </summary>
        public void Attach(T entity, bool asModified)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (asModified)
                _changeTracker.TrackUpdate(entity, typeof(T));
            else
                Attach(entity);
        }

        #endregion

        #region AsNoTracking

        /// <summary>
        /// Returns a query configuration that skips change tracking.
        /// Loaded entities will NOT be tracked for updates. Best for read-only queries.
        /// Inspired by EF Core's AsNoTracking().
        /// </summary>
        public Table<T> AsNoTracking()
        {
            _noTracking = true;
            return this;
        }

        #endregion

        #region Sync Query Methods

        /// <summary>
        /// Finds an entity by primary key value(s).
        /// </summary>
        public T Find(params object[] keyValues)
        {
            return FindInternal(keyValues);
        }

        /// <summary>
        /// Server-side WHERE using LINQ expression predicate.
        /// </summary>
        public IEnumerable<T> Where(Expression<Func<T, bool>> predicate)
        {
            return ExecuteWhere(predicate);
        }

        /// <summary>
        /// Returns first matching entity or null. Uses TOP/LIMIT 1.
        /// </summary>
        public T FirstOrDefault(Expression<Func<T, bool>> predicate)
        {
            return ExecuteWhere(predicate, top: 1).FirstOrDefault();
        }

        /// <summary>
        /// Server-side COUNT matching predicate.
        /// </summary>
        public int Count(Expression<Func<T, bool>> predicate)
        {
            var mapping = MappingCache.GetMapping<T>();
            var builder = new WhereBuilder(mapping);
            var (whereSql, parameters) = builder.Build(predicate);
            var sql = $"SELECT COUNT(*) FROM [{mapping.TableName}] WHERE {whereSql}";
            _context.EnsureConnectionOpen();
            return _context.Connection.ExecuteScalar<int>(sql, ToDp(parameters),
                transaction: _context.Transaction, commandTimeout: _context.CommandTimeout);
        }

        /// <summary>
        /// Server-side existence check.
        /// </summary>
        public bool Any(Expression<Func<T, bool>> predicate) => Count(predicate) > 0;

        public IQueryable<T> AsQueryable() => GetAll().AsQueryable();

        #endregion

        #region Async Query Methods

        /// <summary>
        /// Async Find by primary key.
        /// </summary>
        public async Task<T> FindAsync(params object[] keyValues)
        {
            return await FindInternalAsync(keyValues).ConfigureAwait(false);
        }

        /// <summary>
        /// Async server-side WHERE.
        /// </summary>
        public async Task<List<T>> WhereAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        {
            return await ExecuteWhereAsync(predicate, ct: ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Async FirstOrDefault with TOP/LIMIT 1.
        /// </summary>
        public async Task<T> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        {
            var results = await ExecuteWhereAsync(predicate, top: 1, ct: ct).ConfigureAwait(false);
            return results.FirstOrDefault();
        }

        /// <summary>
        /// Async server-side COUNT.
        /// </summary>
        public async Task<int> CountAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        {
            var mapping = MappingCache.GetMapping<T>();
            var builder = new WhereBuilder(mapping);
            var (whereSql, parameters) = builder.Build(predicate);
            var sql = $"SELECT COUNT(*) FROM [{mapping.TableName}] WHERE {whereSql}";
            await _context.EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            return await _context.Connection.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, ToDp(parameters),
                    transaction: _context.Transaction, commandTimeout: _context.CommandTimeout,
                    cancellationToken: ct)).ConfigureAwait(false);
        }

        /// <summary>
        /// Async server-side existence check.
        /// </summary>
        public async Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        {
            return await CountAsync(predicate, ct).ConfigureAwait(false) > 0;
        }

        /// <summary>
        /// Async load all rows.
        /// </summary>
        public async Task<List<T>> ToListAsync(CancellationToken ct = default)
        {
            await _context.EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            var mapping = MappingCache.GetMapping<T>();
            var sql = SqlGenerator.GenerateSelectAll(mapping);
            var results = (await _context.Connection.QueryAsync<T>(
                new CommandDefinition(sql, transaction: _context.Transaction,
                    commandTimeout: _context.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
            TrackResults(results, mapping);
            return results;
        }

        #endregion

        #region IEnumerable

        public IEnumerator<T> GetEnumerator() => GetAll().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        #endregion

        #region Private: Find by PK

        private T FindInternal(object[] keyValues)
        {
            var mapping = MappingCache.GetMapping<T>();
            var pks = mapping.PrimaryKeys.ToList();
            ValidateKeyValues(pks, keyValues);

            var (sql, dp) = BuildFindSql(mapping, pks, keyValues);
            _context.EnsureConnectionOpen();
            var entity = _context.Connection.QueryFirstOrDefault<T>(sql, dp,
                transaction: _context.Transaction, commandTimeout: _context.CommandTimeout);
            if (entity != null)
            {
                TrackSingle(entity, mapping);
                LoadAssociations(new List<T> { entity }, mapping);
            }
            return entity;
        }

        private async Task<T> FindInternalAsync(object[] keyValues)
        {
            var mapping = MappingCache.GetMapping<T>();
            var pks = mapping.PrimaryKeys.ToList();
            ValidateKeyValues(pks, keyValues);

            var (sql, dp) = BuildFindSql(mapping, pks, keyValues);
            await _context.EnsureConnectionOpenAsync().ConfigureAwait(false);
            var entity = await _context.Connection.QueryFirstOrDefaultAsync<T>(sql, dp,
                transaction: _context.Transaction, commandTimeout: _context.CommandTimeout).ConfigureAwait(false);
            if (entity != null)
            {
                TrackSingle(entity, mapping);
                await LoadAssociationsAsync(new List<T> { entity }, mapping).ConfigureAwait(false);
            }
            return entity;
        }

        private static (string sql, DynamicParameters dp) BuildFindSql(
            EntityMapping mapping, List<ColumnMapping> pks, object[] keyValues)
        {
            var conditions = new List<string>();
            var dp = new DynamicParameters();
            for (int i = 0; i < pks.Count; i++)
            {
                var paramName = $"@pk{i}";
                conditions.Add($"[{pks[i].ColumnName}] = {paramName}");
                dp.Add(paramName, keyValues[i]);
            }
            var sql = $"{SqlGenerator.GenerateSelectAll(mapping)} WHERE {string.Join(" AND ", conditions)}";
            return (sql, dp);
        }

        private static void ValidateKeyValues(List<ColumnMapping> pks, object[] keyValues)
        {
            if (keyValues == null || keyValues.Length != pks.Count)
                throw new ArgumentException(
                    $"Expected {pks.Count} key value(s) for type {typeof(T).Name}, got {keyValues?.Length ?? 0}.");
        }

        #endregion

        #region Private: Where Execution

        private List<T> ExecuteWhere(Expression<Func<T, bool>> predicate, int? top = null)
        {
            var mapping = MappingCache.GetMapping<T>();
            var (fullSql, dp) = BuildWhereSql(mapping, predicate, top);
            _context.EnsureConnectionOpen();
            var results = _context.Connection.Query<T>(fullSql, dp,
                transaction: _context.Transaction, commandTimeout: _context.CommandTimeout).ToList();
            TrackResults(results, mapping);
            LoadAssociations(results, mapping);
            return results;
        }

        private async Task<List<T>> ExecuteWhereAsync(Expression<Func<T, bool>> predicate,
            int? top = null, CancellationToken ct = default)
        {
            var mapping = MappingCache.GetMapping<T>();
            var (fullSql, dp) = BuildWhereSql(mapping, predicate, top);
            await _context.EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            var results = (await _context.Connection.QueryAsync<T>(
                new CommandDefinition(fullSql, dp, transaction: _context.Transaction,
                    commandTimeout: _context.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
            TrackResults(results, mapping);
            await LoadAssociationsAsync(results, mapping, ct).ConfigureAwait(false);
            return results;
        }

        private (string sql, DynamicParameters dp) BuildWhereSql(
            EntityMapping mapping, Expression<Func<T, bool>> predicate, int? top)
        {
            var builder = new WhereBuilder(mapping);
            var (whereSql, parameters) = builder.Build(predicate);
            var fullSql = $"{SqlGenerator.GenerateSelectAll(mapping)} WHERE {whereSql}";

            if (top.HasValue)
            {
                var isSqlite = _context.Connection.GetType().Name
                    .IndexOf("sqlite", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isSqlite)
                    fullSql += $" LIMIT {top.Value}";
                else
                    fullSql = fullSql.Replace("SELECT ", $"SELECT TOP {top.Value} ");
            }

            return (fullSql, ToDp(parameters));
        }

        #endregion

        #region Private: Helpers

        private List<T> GetAll()
        {
            var mapping = MappingCache.GetMapping<T>();
            var results = _context.ExecuteQuery<T>(SqlGenerator.GenerateSelectAll(mapping)).ToList();
            TrackResults(results, mapping);
            LoadAssociations(results, mapping);
            return results;
        }

        private void TrackResults(List<T> results, EntityMapping mapping)
        {
            if (_noTracking || !_context.ObjectTrackingEnabled) return;
            foreach (var entity in results)
                _changeTracker.TrackLoaded(entity, mapping);
        }

        private void TrackSingle(T entity, EntityMapping mapping)
        {
            if (_noTracking || !_context.ObjectTrackingEnabled) return;
            _changeTracker.TrackLoaded(entity, mapping);
        }

        private static DynamicParameters ToDp(IDictionary<string, object> parameters)
        {
            var dp = new DynamicParameters();
            if (parameters != null)
                foreach (var kv in parameters) dp.Add(kv.Key, kv.Value);
            return dp;
        }

        /// <summary>
        /// Batch-loads FK navigation properties for a list of entities.
        /// If LoadOptions is set, only loads registered properties.
        /// If LoadOptions is null, loads ALL FK associations automatically.
        /// Uses IN queries to avoid N+1 problem.
        /// </summary>
        private void LoadAssociations(List<T> entities, EntityMapping mapping)
        {
            if (entities == null || entities.Count == 0) return;
            if (mapping.Associations == null || mapping.Associations.Count == 0) return;

            IEnumerable<AssociationMapping> assocsToLoad;

            if (_context.LoadOptions != null)
            {
                // Selective mode: only load registered properties
                var loadRules = _context.LoadOptions.GetLoadRules(typeof(T));
                if (loadRules.Count == 0) return;
                assocsToLoad = mapping.Associations
                    .Where(a => a.IsForeignKey && loadRules.Any(r => r.Name == a.Property.Name));
            }
            else
            {
                // Auto mode: load ALL FK associations
                assocsToLoad = mapping.Associations.Where(a => a.IsForeignKey);
            }

            foreach (var assoc in assocsToLoad)
            {
                LoadFkAssociation(entities, assoc);
            }
        }

        /// <summary>
        /// Loads a single FK association for all entities using a batch IN query.
        /// </summary>
        private void LoadFkAssociation(List<T> entities, AssociationMapping assoc)
        {
            // Get the FK column property (e.g. idLeader)
            var fkProp = typeof(T).GetProperty(assoc.ThisKey);
            if (fkProp == null) return;

            // Collect all non-null FK values
            var fkValues = entities
                .Select(e => fkProp.GetValue(e))
                .Where(v => v != null && !v.Equals(GetDefault(fkProp.PropertyType)))
                .Distinct()
                .ToList();

            if (fkValues.Count == 0) return;

            // Get related entity mapping
            var relatedMapping = MappingCache.GetMapping(assoc.OtherType);
            var quotedTable = SqlGenerator.QuoteTableName(relatedMapping.TableName);

            // Build batch IN query: SELECT * FROM [dbo].[tbSYS_User] WHERE [id] IN (@p0, @p1, ...)
            var dp = new DynamicParameters();
            var paramNames = new List<string>();
            for (int i = 0; i < fkValues.Count; i++)
            {
                var pName = $"@fk{i}";
                paramNames.Add(pName);
                dp.Add(pName, fkValues[i]);
            }

            var sql = $"SELECT * FROM {quotedTable} WHERE [{assoc.OtherKey}] IN ({string.Join(", ", paramNames)})";

            _context.EnsureConnectionOpen();
            // Query as dynamic, then use Dapper to map
            var relatedEntities = _context.Connection.Query(
                assoc.OtherType, sql, dp,
                transaction: _context.Transaction,
                commandTimeout: _context.CommandTimeout).ToList();

            // Build lookup: OtherKey value → related entity
            var otherKeyProp = assoc.OtherType.GetProperty(assoc.OtherKey);
            if (otherKeyProp == null) return;

            var lookup = new Dictionary<object, object>();
            foreach (var related in relatedEntities)
            {
                var keyVal = otherKeyProp.GetValue(related);
                if (keyVal != null) lookup[keyVal] = related;
            }

            // Set navigation property on each entity
            foreach (var entity in entities)
            {
                var fkVal = fkProp.GetValue(entity);
                if (fkVal != null && lookup.TryGetValue(fkVal, out var relatedEntity))
                {
                    assoc.Property.SetValue(entity, relatedEntity);
                }
            }
        }

        /// <summary>
        /// Async version of LoadAssociations.
        /// </summary>
        private async Task LoadAssociationsAsync(List<T> entities, EntityMapping mapping,
            CancellationToken ct = default)
        {
            if (entities == null || entities.Count == 0) return;
            if (mapping.Associations == null || mapping.Associations.Count == 0) return;

            IEnumerable<AssociationMapping> assocsToLoad;

            if (_context.LoadOptions != null)
            {
                var loadRules = _context.LoadOptions.GetLoadRules(typeof(T));
                if (loadRules.Count == 0) return;
                assocsToLoad = mapping.Associations
                    .Where(a => a.IsForeignKey && loadRules.Any(r => r.Name == a.Property.Name));
            }
            else
            {
                assocsToLoad = mapping.Associations.Where(a => a.IsForeignKey);
            }

            foreach (var assoc in assocsToLoad)
            {
                await LoadFkAssociationAsync(entities, assoc, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Async batch FK loading.
        /// </summary>
        private async Task LoadFkAssociationAsync(List<T> entities, AssociationMapping assoc,
            CancellationToken ct = default)
        {
            var fkProp = typeof(T).GetProperty(assoc.ThisKey);
            if (fkProp == null) return;

            var fkValues = entities
                .Select(e => fkProp.GetValue(e))
                .Where(v => v != null && !v.Equals(GetDefault(fkProp.PropertyType)))
                .Distinct()
                .ToList();

            if (fkValues.Count == 0) return;

            var relatedMapping = MappingCache.GetMapping(assoc.OtherType);
            var quotedTable = SqlGenerator.QuoteTableName(relatedMapping.TableName);

            var dp = new DynamicParameters();
            var paramNames = new List<string>();
            for (int i = 0; i < fkValues.Count; i++)
            {
                var pName = $"@fk{i}";
                paramNames.Add(pName);
                dp.Add(pName, fkValues[i]);
            }

            var sql = $"SELECT * FROM {quotedTable} WHERE [{assoc.OtherKey}] IN ({string.Join(", ", paramNames)})";

            await _context.EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            var relatedEntities = (await _context.Connection.QueryAsync(
                assoc.OtherType, sql, dp,
                transaction: _context.Transaction,
                commandTimeout: _context.CommandTimeout).ConfigureAwait(false)).ToList();

            var otherKeyProp = assoc.OtherType.GetProperty(assoc.OtherKey);
            if (otherKeyProp == null) return;

            var lookup = new Dictionary<object, object>();
            foreach (var related in relatedEntities)
            {
                var keyVal = otherKeyProp.GetValue(related);
                if (keyVal != null) lookup[keyVal] = related;
            }

            foreach (var entity in entities)
            {
                var fkVal = fkProp.GetValue(entity);
                if (fkVal != null && lookup.TryGetValue(fkVal, out var relatedEntity))
                {
                    assoc.Property.SetValue(entity, relatedEntity);
                }
            }
        }

        private static object GetDefault(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        #endregion
    }
}
