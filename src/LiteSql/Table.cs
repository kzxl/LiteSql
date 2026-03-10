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
        private List<string> _orderByClauses;
        private int? _skip;
        private int? _take;

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

        #region OrderBy / ThenBy

        /// <summary>
        /// Sorts the results in ascending order by the specified column.
        /// Chain with Where(), ToList(), FirstOrDefault() etc.
        /// Example: db.GetTable&lt;T&gt;().OrderBy(x => x.Date).Where(x => x.Active)
        /// </summary>
        public Table<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector)
        {
            _orderByClauses = new List<string>();
            _orderByClauses.Add($"[{ExtractColumnName(keySelector)}] ASC");
            return this;
        }

        /// <summary>
        /// Sorts the results in descending order by the specified column.
        /// </summary>
        public Table<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
        {
            _orderByClauses = new List<string>();
            _orderByClauses.Add($"[{ExtractColumnName(keySelector)}] DESC");
            return this;
        }

        /// <summary>
        /// Adds a secondary ascending sort. Must be called after OrderBy/OrderByDescending.
        /// </summary>
        public Table<T> ThenBy<TKey>(Expression<Func<T, TKey>> keySelector)
        {
            if (_orderByClauses == null)
                throw new InvalidOperationException("ThenBy must be called after OrderBy or OrderByDescending.");
            _orderByClauses.Add($"[{ExtractColumnName(keySelector)}] ASC");
            return this;
        }

        /// <summary>
        /// Adds a secondary descending sort. Must be called after OrderBy/OrderByDescending.
        /// </summary>
        public Table<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
        {
            if (_orderByClauses == null)
                throw new InvalidOperationException("ThenByDescending must be called after OrderBy or OrderByDescending.");
            _orderByClauses.Add($"[{ExtractColumnName(keySelector)}] DESC");
            return this;
        }

        #endregion

        #region Skip / Take

        /// <summary>
        /// Skips N rows. For SQL Server, OrderBy is required.
        /// </summary>
        public Table<T> Skip(int count)
        {
            _skip = count;
            return this;
        }

        /// <summary>
        /// Takes at most N rows.
        /// </summary>
        public Table<T> Take(int count)
        {
            _take = count;
            return this;
        }

        #endregion

        #region Include (selective FK loading)

        /// <summary>
        /// Starts a selective FK loading query.
        /// Only the specified navigation properties will be loaded (not all FKs).
        /// Chain multiple Include() calls for multiple FKs.
        /// Example: db.Orders.Include(o => o.Customer).Include(o => o.Product).Where(...)
        /// </summary>
        public IncludeQuery<T> Include(Expression<Func<T, object>> expression)
        {
            var query = new IncludeQuery<T>(this, _context, _changeTracker);
            return query.Include(expression);
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
        public List<T> Where(Expression<Func<T, bool>> predicate)
        {
            return ExecuteWhere(predicate);
        }

        /// <summary>
        /// Returns first matching entity or null. Uses TOP/LIMIT 1.
        /// </summary>
        public T FirstOrDefault(Expression<Func<T, bool>> predicate)
        {
            if (_take == null) _take = 1;
            return ExecuteWhere(predicate).FirstOrDefault();
        }

        /// <summary>
        /// Executes the current query and returns all matching rows as a List.
        /// Respects OrderBy/Skip/Take if set.
        /// </summary>
        public List<T> ToList()
        {
            if (_orderByClauses != null || _skip.HasValue || _take.HasValue)
                return ExecuteWhere(null);
            return GetAll();
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

        /// <summary>
        /// Server-side MAX aggregate.
        /// </summary>
        public TResult Max<TResult>(Expression<Func<T, TResult>> selector)
            => ExecuteAggregate<TResult>("MAX", selector, null);

        /// <summary>
        /// Server-side MAX with WHERE.
        /// </summary>
        public TResult Max<TResult>(Expression<Func<T, TResult>> selector, Expression<Func<T, bool>> predicate)
            => ExecuteAggregate<TResult>("MAX", selector, predicate);

        /// <summary>
        /// Server-side MIN aggregate.
        /// </summary>
        public TResult Min<TResult>(Expression<Func<T, TResult>> selector)
            => ExecuteAggregate<TResult>("MIN", selector, null);

        /// <summary>
        /// Server-side MIN with WHERE.
        /// </summary>
        public TResult Min<TResult>(Expression<Func<T, TResult>> selector, Expression<Func<T, bool>> predicate)
            => ExecuteAggregate<TResult>("MIN", selector, predicate);

        /// <summary>
        /// Server-side SUM aggregate.
        /// </summary>
        public TResult Sum<TResult>(Expression<Func<T, TResult>> selector)
            => ExecuteAggregate<TResult>("SUM", selector, null);

        /// <summary>
        /// Server-side SUM with WHERE.
        /// </summary>
        public TResult Sum<TResult>(Expression<Func<T, TResult>> selector, Expression<Func<T, bool>> predicate)
            => ExecuteAggregate<TResult>("SUM", selector, predicate);

        /// <summary>
        /// Server-side AVERAGE aggregate.
        /// </summary>
        public TResult Average<TResult>(Expression<Func<T, TResult>> selector)
            => ExecuteAggregate<TResult>("AVG", selector, null);

        /// <summary>
        /// Server-side DISTINCT.
        /// </summary>
        public List<T> Distinct()
        {
            var mapping = MappingCache.GetMapping<T>();
            var sql = SqlGenerator.GenerateSelectAll(mapping).Replace("SELECT ", "SELECT DISTINCT ");
            _context.EnsureConnectionOpen();
            return _context.Connection.Query<T>(sql,
                transaction: _context.Transaction, commandTimeout: _context.CommandTimeout).ToList();
        }

        public IQueryable<T> AsQueryable() => GetAll().AsQueryable();

        /// <summary>
        /// Server-side SELECT projection. Translates expression into SQL column list.
        /// Supports anonymous types, DTOs, and scalar properties.
        /// </summary>
        public List<TResult> Select<TResult>(Expression<Func<T, TResult>> selector)
        {
            return ExecuteSelect(selector, null);
        }

        /// <summary>
        /// Server-side SELECT projection with WHERE clause.
        /// </summary>
        public List<TResult> Select<TResult>(Expression<Func<T, TResult>> selector,
            Expression<Func<T, bool>> predicate)
        {
            return ExecuteSelect(selector, predicate);
        }

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
            if (_take == null) _take = 1;
            var results = await ExecuteWhereAsync(predicate, ct: ct).ConfigureAwait(false);
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
        /// Async MAX aggregate.
        /// </summary>
        public async Task<TResult> MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default)
            => await ExecuteAggregateAsync<TResult>("MAX", selector, null, ct).ConfigureAwait(false);

        /// <summary>
        /// Async MIN aggregate.
        /// </summary>
        public async Task<TResult> MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default)
            => await ExecuteAggregateAsync<TResult>("MIN", selector, null, ct).ConfigureAwait(false);

        /// <summary>
        /// Async SUM aggregate.
        /// </summary>
        public async Task<TResult> SumAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default)
            => await ExecuteAggregateAsync<TResult>("SUM", selector, null, ct).ConfigureAwait(false);

        /// <summary>
        /// Async DISTINCT.
        /// </summary>
        public async Task<List<T>> DistinctAsync(CancellationToken ct = default)
        {
            var mapping = MappingCache.GetMapping<T>();
            var sql = SqlGenerator.GenerateSelectAll(mapping).Replace("SELECT ", "SELECT DISTINCT ");
            await _context.EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            return (await _context.Connection.QueryAsync<T>(
                new CommandDefinition(sql, transaction: _context.Transaction,
                    commandTimeout: _context.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        }

        /// <summary>
        /// Async load all rows. Respects OrderBy/Skip/Take if set.
        /// </summary>
        public async Task<List<T>> ToListAsync(CancellationToken ct = default)
        {
            if (_orderByClauses != null || _skip.HasValue || _take.HasValue)
                return await ExecuteWhereAsync(null, ct: ct).ConfigureAwait(false);

            await _context.EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            var mapping = MappingCache.GetMapping<T>();
            var sql = SqlGenerator.GenerateSelectAll(mapping);
            var results = (await _context.Connection.QueryAsync<T>(
                new CommandDefinition(sql, transaction: _context.Transaction,
                    commandTimeout: _context.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
            TrackResults(results, mapping);
            await LoadAssociationsAsync(results, mapping, ct).ConfigureAwait(false);
            return results;
        }

        /// <summary>
        /// Async server-side SELECT projection.
        /// </summary>
        public async Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T, TResult>> selector,
            CancellationToken ct = default)
        {
            return await ExecuteSelectAsync(selector, null, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Async server-side SELECT projection with WHERE clause.
        /// </summary>
        public async Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T, TResult>> selector,
            Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        {
            return await ExecuteSelectAsync(selector, predicate, ct).ConfigureAwait(false);
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

        private List<T> ExecuteWhere(Expression<Func<T, bool>> predicate)
        {
            var mapping = MappingCache.GetMapping<T>();
            var orderBy = _orderByClauses;
            var skip = _skip;
            var take = _take;
            ResetQueryState();

            var (fullSql, dp) = BuildWhereSql(mapping, predicate, orderBy, skip, take);
            _context.EnsureConnectionOpen();
            var results = _context.Connection.Query<T>(fullSql, dp,
                transaction: _context.Transaction, commandTimeout: _context.CommandTimeout).ToList();
            TrackResults(results, mapping);
            LoadAssociations(results, mapping);
            return results;
        }

        private async Task<List<T>> ExecuteWhereAsync(Expression<Func<T, bool>> predicate,
            CancellationToken ct = default)
        {
            var mapping = MappingCache.GetMapping<T>();
            var orderBy = _orderByClauses;
            var skip = _skip;
            var take = _take;
            ResetQueryState();

            var (fullSql, dp) = BuildWhereSql(mapping, predicate, orderBy, skip, take);
            await _context.EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            var results = (await _context.Connection.QueryAsync<T>(
                new CommandDefinition(fullSql, dp, transaction: _context.Transaction,
                    commandTimeout: _context.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
            TrackResults(results, mapping);
            await LoadAssociationsAsync(results, mapping, ct).ConfigureAwait(false);
            return results;
        }

        private (string sql, DynamicParameters dp) BuildWhereSql(
            EntityMapping mapping, Expression<Func<T, bool>> predicate,
            List<string> orderByClauses, int? skip, int? take)
        {
            var selectAll = SqlGenerator.GenerateSelectAll(mapping);
            IDictionary<string, object> parameters = null;

            string fullSql;
            if (predicate != null)
            {
                var builder = new WhereBuilder(mapping);
                var (whereSql, whereParams) = builder.Build(predicate);
                parameters = whereParams;
                fullSql = $"{selectAll} WHERE {whereSql}";
            }
            else
            {
                fullSql = selectAll;
            }

            // ORDER BY
            if (orderByClauses != null && orderByClauses.Count > 0)
            {
                fullSql += $" ORDER BY {string.Join(", ", orderByClauses)}";
            }

            // Pagination
            var isSqlite = _context.Connection.GetType().Name
                .IndexOf("sqlite", StringComparison.OrdinalIgnoreCase) >= 0;

            if (skip.HasValue || take.HasValue)
            {
                if (isSqlite)
                {
                    // SQLite: LIMIT {take} OFFSET {skip}
                    if (take.HasValue)
                        fullSql += $" LIMIT {take.Value}";
                    else
                        fullSql += " LIMIT -1"; // unlimited
                    if (skip.HasValue)
                        fullSql += $" OFFSET {skip.Value}";
                }
                else
                {
                    // SQL Server: OFFSET ... ROWS FETCH NEXT ... ROWS ONLY
                    // ORDER BY is required for OFFSET/FETCH
                    if (orderByClauses == null || orderByClauses.Count == 0)
                        fullSql += " ORDER BY (SELECT NULL)";

                    fullSql += $" OFFSET {skip ?? 0} ROWS";
                    if (take.HasValue)
                        fullSql += $" FETCH NEXT {take.Value} ROWS ONLY";
                }
            }
            else if (take.HasValue && orderByClauses == null)
            {
                // Legacy TOP behavior for FirstOrDefault without OrderBy
                if (isSqlite)
                    fullSql += $" LIMIT {take.Value}";
                else
                    fullSql = fullSql.Replace("SELECT ", $"SELECT TOP {take.Value} ");
            }

            return (fullSql, ToDp(parameters));
        }

        #endregion

        #region Private: Select Projection Execution

        private List<TResult> ExecuteSelect<TResult>(Expression<Func<T, TResult>> selector,
            Expression<Func<T, bool>> predicate)
        {
            var mapping = MappingCache.GetMapping<T>();
            var orderBy = _orderByClauses;
            var skip = _skip;
            var take = _take;
            ResetQueryState();

            var (fullSql, dp) = BuildSelectSql(mapping, selector, predicate, orderBy, skip, take);
            _context.EnsureConnectionOpen();
            return _context.Connection.Query<TResult>(fullSql, dp,
                transaction: _context.Transaction, commandTimeout: _context.CommandTimeout).ToList();
        }

        private async Task<List<TResult>> ExecuteSelectAsync<TResult>(Expression<Func<T, TResult>> selector,
            Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        {
            var mapping = MappingCache.GetMapping<T>();
            var orderBy = _orderByClauses;
            var skip = _skip;
            var take = _take;
            ResetQueryState();

            var (fullSql, dp) = BuildSelectSql(mapping, selector, predicate, orderBy, skip, take);
            await _context.EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            var results = (await _context.Connection.QueryAsync<TResult>(
                new CommandDefinition(fullSql, dp, transaction: _context.Transaction,
                    commandTimeout: _context.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
            return results;
        }

        private (string sql, DynamicParameters dp) BuildSelectSql<TResult>(
            EntityMapping mapping, Expression<Func<T, TResult>> selector,
            Expression<Func<T, bool>> predicate,
            List<string> orderByClauses, int? skip, int? take)
        {
            var selectBuilder = new SelectBuilder(mapping);
            var columnList = selectBuilder.Build(selector);
            var tableName = SqlGenerator.QuoteTableName(mapping.TableName);

            IDictionary<string, object> parameters = null;
            string fullSql;
            if (predicate != null)
            {
                var builder = new WhereBuilder(mapping);
                var (whereSql, whereParams) = builder.Build(predicate);
                parameters = whereParams;
                fullSql = $"SELECT {columnList} FROM {tableName} WHERE {whereSql}";
            }
            else
            {
                fullSql = $"SELECT {columnList} FROM {tableName}";
            }

            // ORDER BY
            if (orderByClauses != null && orderByClauses.Count > 0)
                fullSql += $" ORDER BY {string.Join(", ", orderByClauses)}";

            // Pagination — reuse same logic as BuildWhereSql
            var isSqlite = _context.Connection.GetType().Name
                .IndexOf("sqlite", StringComparison.OrdinalIgnoreCase) >= 0;

            if (skip.HasValue || take.HasValue)
            {
                if (isSqlite)
                {
                    if (take.HasValue) fullSql += $" LIMIT {take.Value}";
                    else fullSql += " LIMIT -1";
                    if (skip.HasValue) fullSql += $" OFFSET {skip.Value}";
                }
                else
                {
                    if (orderByClauses == null || orderByClauses.Count == 0)
                        fullSql += " ORDER BY (SELECT NULL)";
                    fullSql += $" OFFSET {skip ?? 0} ROWS";
                    if (take.HasValue)
                        fullSql += $" FETCH NEXT {take.Value} ROWS ONLY";
                }
            }

            return (fullSql, ToDp(parameters));
        }

        #endregion

        #region Private: Aggregate Execution

        private TResult ExecuteAggregate<TResult>(string function,
            LambdaExpression selector, Expression<Func<T, bool>> predicate)
        {
            var mapping = MappingCache.GetMapping<T>();
            var columnName = ExtractColumnNameFromLambda(selector, mapping);
            var tableName = SqlGenerator.QuoteTableName(mapping.TableName);

            string sql;
            IDictionary<string, object> parameters = null;
            if (predicate != null)
            {
                var builder = new WhereBuilder(mapping);
                var (whereSql, whereParams) = builder.Build(predicate);
                parameters = whereParams;
                sql = $"SELECT {function}([{columnName}]) FROM {tableName} WHERE {whereSql}";
            }
            else
            {
                sql = $"SELECT {function}([{columnName}]) FROM {tableName}";
            }

            _context.EnsureConnectionOpen();
            return _context.Connection.ExecuteScalar<TResult>(sql, ToDp(parameters),
                transaction: _context.Transaction, commandTimeout: _context.CommandTimeout);
        }

        private async Task<TResult> ExecuteAggregateAsync<TResult>(string function,
            LambdaExpression selector, Expression<Func<T, bool>> predicate, CancellationToken ct)
        {
            var mapping = MappingCache.GetMapping<T>();
            var columnName = ExtractColumnNameFromLambda(selector, mapping);
            var tableName = SqlGenerator.QuoteTableName(mapping.TableName);

            string sql;
            IDictionary<string, object> parameters = null;
            if (predicate != null)
            {
                var builder = new WhereBuilder(mapping);
                var (whereSql, whereParams) = builder.Build(predicate);
                parameters = whereParams;
                sql = $"SELECT {function}([{columnName}]) FROM {tableName} WHERE {whereSql}";
            }
            else
            {
                sql = $"SELECT {function}([{columnName}]) FROM {tableName}";
            }

            await _context.EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            return await _context.Connection.ExecuteScalarAsync<TResult>(
                new CommandDefinition(sql, ToDp(parameters),
                    transaction: _context.Transaction, commandTimeout: _context.CommandTimeout,
                    cancellationToken: ct)).ConfigureAwait(false);
        }

        #endregion

        #region Private: Query State

        private void ResetQueryState()
        {
            _orderByClauses = null;
            _skip = null;
            _take = null;
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

            // Load many-to-one FK associations
            if (mapping.Associations != null && mapping.Associations.Count > 0)
            {
                IEnumerable<AssociationMapping> assocsToLoad;

                if (_context.LoadOptions != null)
                {
                    var loadRules = _context.LoadOptions.GetLoadRules(typeof(T));
                    if (loadRules.Count > 0)
                    {
                        assocsToLoad = mapping.Associations
                            .Where(a => a.IsForeignKey && loadRules.Any(r => r.Name == a.Property.Name));
                        foreach (var assoc in assocsToLoad)
                            LoadFkAssociation(entities, assoc);
                    }
                }
                else
                {
                    assocsToLoad = mapping.Associations.Where(a => a.IsForeignKey);
                    foreach (var assoc in assocsToLoad)
                        LoadFkAssociation(entities, assoc);
                }
            }

            // Load one-to-many collection associations
            if (mapping.CollectionAssociations != null && mapping.CollectionAssociations.Count > 0)
            {
                IEnumerable<AssociationMapping> colAssocsToLoad;

                if (_context.LoadOptions != null)
                {
                    var loadRules = _context.LoadOptions.GetLoadRules(typeof(T));
                    colAssocsToLoad = mapping.CollectionAssociations
                        .Where(a => loadRules.Any(r => r.Name == a.Property.Name));
                }
                else
                {
                    colAssocsToLoad = mapping.CollectionAssociations;
                }

                foreach (var assoc in colAssocsToLoad)
                    LoadCollectionAssociation(entities, assoc);
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

            // Load many-to-one FK associations
            if (mapping.Associations != null && mapping.Associations.Count > 0)
            {
                IEnumerable<AssociationMapping> assocsToLoad;

                if (_context.LoadOptions != null)
                {
                    var loadRules = _context.LoadOptions.GetLoadRules(typeof(T));
                    if (loadRules.Count > 0)
                    {
                        assocsToLoad = mapping.Associations
                            .Where(a => a.IsForeignKey && loadRules.Any(r => r.Name == a.Property.Name));
                        foreach (var assoc in assocsToLoad)
                            await LoadFkAssociationAsync(entities, assoc, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    assocsToLoad = mapping.Associations.Where(a => a.IsForeignKey);
                    foreach (var assoc in assocsToLoad)
                        await LoadFkAssociationAsync(entities, assoc, ct).ConfigureAwait(false);
                }
            }

            // Load one-to-many collection associations
            if (mapping.CollectionAssociations != null && mapping.CollectionAssociations.Count > 0)
            {
                IEnumerable<AssociationMapping> colAssocsToLoad;

                if (_context.LoadOptions != null)
                {
                    var loadRules = _context.LoadOptions.GetLoadRules(typeof(T));
                    colAssocsToLoad = mapping.CollectionAssociations
                        .Where(a => loadRules.Any(r => r.Name == a.Property.Name));
                }
                else
                {
                    colAssocsToLoad = mapping.CollectionAssociations;
                }

                foreach (var assoc in colAssocsToLoad)
                    await LoadCollectionAssociationAsync(entities, assoc, ct).ConfigureAwait(false);
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

        /// <summary>
        /// Loads a one-to-many collection association for all entities using a batch IN query.
        /// Groups children by FK value and sets the collection property.
        /// </summary>
        private void LoadCollectionAssociation(List<T> entities, AssociationMapping assoc)
        {
            // ThisKey is PK on parent, OtherKey is FK on child
            var pkProp = typeof(T).GetProperty(assoc.ThisKey);
            if (pkProp == null) return;

            var pkValues = entities
                .Select(e => pkProp.GetValue(e))
                .Where(v => v != null && !v.Equals(GetDefault(pkProp.PropertyType)))
                .Distinct()
                .ToList();

            if (pkValues.Count == 0) return;

            var childMapping = MappingCache.GetMapping(assoc.OtherType);
            var quotedTable = SqlGenerator.QuoteTableName(childMapping.TableName);

            var dp = new DynamicParameters();
            var paramNames = new List<string>();
            for (int i = 0; i < pkValues.Count; i++)
            {
                var pName = $"@ck{i}";
                paramNames.Add(pName);
                dp.Add(pName, pkValues[i]);
            }

            var sql = $"SELECT * FROM {quotedTable} WHERE [{assoc.OtherKey}] IN ({string.Join(", ", paramNames)})";

            _context.EnsureConnectionOpen();
            var children = _context.Connection.Query(
                assoc.OtherType, sql, dp,
                transaction: _context.Transaction,
                commandTimeout: _context.CommandTimeout).ToList();

            // Group children by FK value
            var fkPropOnChild = assoc.OtherType.GetProperty(assoc.OtherKey);
            if (fkPropOnChild == null) return;

            var grouped = new Dictionary<object, System.Collections.IList>();
            foreach (var child in children)
            {
                var fkVal = fkPropOnChild.GetValue(child);
                if (fkVal == null) continue;
                if (!grouped.TryGetValue(fkVal, out var list))
                {
                    list = (System.Collections.IList)Activator.CreateInstance(
                        typeof(List<>).MakeGenericType(assoc.OtherType));
                    grouped[fkVal] = list;
                }
                list.Add(child);
            }

            // Set collection on each parent
            foreach (var entity in entities)
            {
                var pkVal = pkProp.GetValue(entity);
                if (pkVal != null && grouped.TryGetValue(pkVal, out var childList))
                {
                    assoc.Property.SetValue(entity, childList);
                }
                else
                {
                    // Set empty list if no children found
                    var emptyList = (System.Collections.IList)Activator.CreateInstance(
                        typeof(List<>).MakeGenericType(assoc.OtherType));
                    assoc.Property.SetValue(entity, emptyList);
                }
            }
        }

        /// <summary>
        /// Async version of LoadCollectionAssociation.
        /// </summary>
        private async Task LoadCollectionAssociationAsync(List<T> entities, AssociationMapping assoc,
            CancellationToken ct = default)
        {
            var pkProp = typeof(T).GetProperty(assoc.ThisKey);
            if (pkProp == null) return;

            var pkValues = entities
                .Select(e => pkProp.GetValue(e))
                .Where(v => v != null && !v.Equals(GetDefault(pkProp.PropertyType)))
                .Distinct()
                .ToList();

            if (pkValues.Count == 0) return;

            var childMapping = MappingCache.GetMapping(assoc.OtherType);
            var quotedTable = SqlGenerator.QuoteTableName(childMapping.TableName);

            var dp = new DynamicParameters();
            var paramNames = new List<string>();
            for (int i = 0; i < pkValues.Count; i++)
            {
                var pName = $"@ck{i}";
                paramNames.Add(pName);
                dp.Add(pName, pkValues[i]);
            }

            var sql = $"SELECT * FROM {quotedTable} WHERE [{assoc.OtherKey}] IN ({string.Join(", ", paramNames)})";

            await _context.EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            var children = (await _context.Connection.QueryAsync(
                assoc.OtherType, sql, dp,
                transaction: _context.Transaction,
                commandTimeout: _context.CommandTimeout).ConfigureAwait(false)).ToList();

            var fkPropOnChild = assoc.OtherType.GetProperty(assoc.OtherKey);
            if (fkPropOnChild == null) return;

            var grouped = new Dictionary<object, System.Collections.IList>();
            foreach (var child in children)
            {
                var fkVal = fkPropOnChild.GetValue(child);
                if (fkVal == null) continue;
                if (!grouped.TryGetValue(fkVal, out var list))
                {
                    list = (System.Collections.IList)Activator.CreateInstance(
                        typeof(List<>).MakeGenericType(assoc.OtherType));
                    grouped[fkVal] = list;
                }
                list.Add(child);
            }

            foreach (var entity in entities)
            {
                var pkVal = pkProp.GetValue(entity);
                if (pkVal != null && grouped.TryGetValue(pkVal, out var childList))
                {
                    assoc.Property.SetValue(entity, childList);
                }
                else
                {
                    var emptyList = (System.Collections.IList)Activator.CreateInstance(
                        typeof(List<>).MakeGenericType(assoc.OtherType));
                    assoc.Property.SetValue(entity, emptyList);
                }
            }
        }

        private static object GetDefault(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        /// <summary>
        /// Extracts column name from a LambdaExpression using provided mapping.
        /// Used by aggregate helpers.
        /// </summary>
        private static string ExtractColumnNameFromLambda(LambdaExpression expression, EntityMapping mapping)
        {
            var body = expression.Body;
            if (body is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
                body = unary.Operand;

            if (body is MemberExpression member)
            {
                var propertyName = member.Member.Name;
                var col = mapping.Columns.FirstOrDefault(c => c.Property.Name == propertyName);
                return col?.ColumnName ?? propertyName;
            }

            throw new ArgumentException(
                $"Expression must be a member access (e.g. x => x.Property), got: {expression}");
        }

        /// <summary>
        /// Extracts the database column name from a member access expression.
        /// Uses MappingCache to resolve [Column(Name=...)] attribute.
        /// </summary>
        internal static string ExtractColumnName<TKey>(Expression<Func<T, TKey>> expression)
        {
            var body = expression.Body;
            // Handle Convert (boxing) for value types
            if (body is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
                body = unary.Operand;

            if (body is MemberExpression member)
            {
                var propertyName = member.Member.Name;
                var mapping = MappingCache.GetMapping<T>();
                var col = mapping.Columns.FirstOrDefault(c => c.Property.Name == propertyName);
                return col?.ColumnName ?? propertyName;
            }

            throw new ArgumentException(
                $"Expression must be a member access (e.g. x => x.Property), got: {expression}");
        }

        #endregion
    }
}
