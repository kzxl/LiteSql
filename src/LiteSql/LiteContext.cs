using Dapper;
using LiteSql.ChangeTracking;
using LiteSql.Dialects;
using LiteSql.Mapping;
using LiteSql.Sql;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LiteSql
{
    /// <summary>
    /// Lightweight DataContext replacement for .NET Core, backed by Dapper.
    /// Compatible with System.Data.Linq.DataContext API surface.
    /// Provides both sync and async APIs.
    /// </summary>
    public class LiteContext : IDisposable
    {
        private readonly bool _ownsConnection;
        private readonly ConcurrentDictionary<Type, object> _tables
            = new ConcurrentDictionary<Type, object>();
        private readonly ChangeTracker _changeTracker = new ChangeTracker();
        private bool _disposed;
        private ISqlDialect _dialect;

        /// <summary>
        /// Provides lifecycle hooks for entity save operations.
        /// Register OnBeforeSave/OnAfterSave handlers per entity type.
        /// </summary>
        public SaveHooks Hooks { get; } = new SaveHooks();

        /// <summary>
        /// Exposes ChangeTracker API for querying entity states.
        /// </summary>
        public ChangeTracker ChangeTracker => _changeTracker;

        /// <summary>
        /// Global query filters applied automatically to all queries.
        /// Register per-type predicates (e.g., soft delete, multi-tenant).
        /// </summary>
        public QueryFilterCollection Filters { get; } = new QueryFilterCollection();

        /// <summary>
        /// Query profiler for monitoring execution times and detecting slow queries.
        /// </summary>
        public QueryProfiler Profiler { get; } = new QueryProfiler();

        /// <summary>
        /// Query execution pipeline interceptor.
        /// Provides OnBeforeExecute/OnAfterExecute/OnError hooks.
        /// </summary>
        public QueryInterceptor Interceptor { get; set; }

        /// <summary>
        /// Value converter registry.
        /// Converts model types ⇄ database types (e.g., Enum ↔ string, object ↔ JSON).
        /// </summary>
        public ValueConverterCollection Converters { get; } = new ValueConverterCollection();

        #region Constructors

        public LiteContext(IDbConnection connection, ISqlDialect dialect = null)
        {
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _dialect = dialect ?? SqlDialectFactory.GetDialect(connection);
            _ownsConnection = false;
        }

        public LiteContext(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentNullException(nameof(connectionString));

            Connection = ConnectionFactory?.Invoke(connectionString)
                ?? throw new InvalidOperationException(
                    "LiteContext.ConnectionFactory must be set before using the string constructor. " +
                    "Example: LiteContext.ConnectionFactory = cs => new SqlConnection(cs);");
            _dialect = SqlDialectFactory.GetDialect(Connection);
            _ownsConnection = true;
        }

        #endregion

        #region Static Configuration

        public static Func<string, IDbConnection> ConnectionFactory { get; set; }

        #endregion

        #region Properties

        public IDbConnection Connection { get; }
        public IDbTransaction Transaction { get; set; }
        public int CommandTimeout { get; set; } = 30;
        public TextWriter Log { get; set; }
        public bool ObjectTrackingEnabled { get; set; } = true;

        /// <summary>
        /// Gets the SQL dialect used by this context.
        /// </summary>
        public ISqlDialect Dialect => _dialect;

        /// <summary>
        /// Specifies which navigation properties to eagerly load when querying.
        /// Compatible with System.Data.Linq.DataLoadOptions.
        /// </summary>
        public DataLoadOptions LoadOptions { get; set; }

        #endregion

        #region Core Sync Methods

        public Table<T> GetTable<T>() where T : class
        {
            ThrowIfDisposed();
            return (Table<T>)_tables.GetOrAdd(typeof(T), _ => new Table<T>(this, _changeTracker));
        }

        public void SubmitChanges()
        {
            ThrowIfDisposed();
            DetectAllChanges();
            var changes = _changeTracker.GetPendingChanges();
            if (changes.Count == 0) return;

            EnsureConnectionOpen();
            var ownTx = Transaction == null;
            var tx = Transaction ?? Connection.BeginTransaction();
            try
            {
                ProcessChanges(changes, tx);
                if (ownTx) tx.Commit();
                _changeTracker.AcceptChanges();
            }
            catch { if (ownTx) tx.Rollback(); throw; }
            finally { if (ownTx) tx.Dispose(); }
        }

        public IEnumerable<T> ExecuteQuery<T>(string query, params object[] parameters)
        {
            ThrowIfDisposed();
            EnsureConnectionOpen();
            var (sql, dp) = ConvertParams(query, parameters);
            return Connection.Query<T>(sql, (object)dp, transaction: Transaction, commandTimeout: CommandTimeout);
        }

        public int ExecuteCommand(string command, params object[] parameters)
        {
            ThrowIfDisposed();
            EnsureConnectionOpen();
            var (sql, dp) = ConvertParams(command, parameters);
            return Connection.Execute(sql, (object)dp, transaction: Transaction, commandTimeout: CommandTimeout);
        }

        /// <summary>
        /// Executes raw SQL and maps results to T using named @parameters.
        /// Unlike ExecuteQuery which uses positional {0} params,
        /// this accepts an anonymous object for named parameters.
        /// Example: db.FromSql&lt;Order&gt;("SELECT * FROM Orders WHERE Status = @status", new { status = "Active" })
        /// </summary>
        public List<T> FromSql<T>(string sql, object parameters = null)
        {
            ThrowIfDisposed();
            EnsureConnectionOpen();
            return Connection.Query<T>(sql, parameters,
                transaction: Transaction, commandTimeout: CommandTimeout).ToList();
        }

        /// <summary>
        /// Async version of FromSql.
        /// </summary>
        public async Task<List<T>> FromSqlAsync<T>(string sql, object parameters = null,
            CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            return (await Connection.QueryAsync<T>(
                new CommandDefinition(sql, parameters,
                    transaction: Transaction, commandTimeout: CommandTimeout,
                    cancellationToken: ct)).ConfigureAwait(false)).ToList();
        }

        #endregion

        #region Core Async Methods

        public async Task SubmitChangesAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            DetectAllChanges();
            var changes = _changeTracker.GetPendingChanges();
            if (changes.Count == 0) return;

            await EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            var ownTx = Transaction == null;
            var tx = Transaction ?? Connection.BeginTransaction();
            try
            {
                await ProcessChangesAsync(changes, tx, ct).ConfigureAwait(false);
                if (ownTx) tx.Commit();
                _changeTracker.AcceptChanges();
            }
            catch { if (ownTx) tx.Rollback(); throw; }
            finally { if (ownTx) tx.Dispose(); }
        }

        public async Task<IEnumerable<T>> ExecuteQueryAsync<T>(string query, params object[] parameters)
        {
            ThrowIfDisposed();
            await EnsureConnectionOpenAsync().ConfigureAwait(false);
            var (sql, dp) = ConvertParams(query, parameters);
            return await Connection.QueryAsync<T>(sql, (object)dp,
                transaction: Transaction, commandTimeout: CommandTimeout).ConfigureAwait(false);
        }

        public async Task<int> ExecuteCommandAsync(string command, params object[] parameters)
        {
            ThrowIfDisposed();
            await EnsureConnectionOpenAsync().ConfigureAwait(false);
            var (sql, dp) = ConvertParams(command, parameters);
            return await Connection.ExecuteAsync(sql, (object)dp,
                transaction: Transaction, commandTimeout: CommandTimeout).ConfigureAwait(false);
        }

        #endregion

        #region Transaction Helpers (Phase 9)

        /// <summary>
        /// Executes an action within an auto-managed transaction.
        /// Commits on success, rolls back on exception.
        /// </summary>
        public void ExecuteInTransaction(Action<LiteContext> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            ThrowIfDisposed();
            EnsureConnectionOpen();
            using (var tx = Connection.BeginTransaction())
            {
                Transaction = tx;
                try
                {
                    action(this);
                    tx.Commit();
                }
                catch { tx.Rollback(); throw; }
                finally { Transaction = null; }
            }
        }

        /// <summary>
        /// Executes an async action within an auto-managed transaction.
        /// Commits on success, rolls back on exception.
        /// </summary>
        public async Task ExecuteInTransactionAsync(Func<LiteContext, Task> action, CancellationToken ct = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            ThrowIfDisposed();
            await EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            using (var tx = Connection.BeginTransaction())
            {
                Transaction = tx;
                try
                {
                    await action(this).ConfigureAwait(false);
                    tx.Commit();
                }
                catch { tx.Rollback(); throw; }
                finally { Transaction = null; }
            }
        }

        #endregion

        #region InsertAndGetId (Phase 8.2)

        /// <summary>
        /// Inserts an entity and immediately returns the generated identity value.
        /// Does not go through SubmitChanges — executes immediately.
        /// </summary>
        public long InsertAndGetId<T>(T entity) where T : class
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            ThrowIfDisposed();
            EnsureConnectionOpen();

            var mapping = MappingCache.GetMapping<T>();
            var (sql, parameters) = SqlGenerator.GenerateInsert(mapping, entity, _dialect);
            LogSql(sql, parameters);
            Connection.Execute(sql, (object)ToDynamicParameters(parameters),
                transaction: Transaction, commandTimeout: CommandTimeout);

            var pk = mapping.PrimaryKeys.FirstOrDefault(p => p.IsDbGenerated);
            var id = Connection.ExecuteScalar<long>(_dialect.GetLastInsertIdSql(mapping.TableName, pk?.ColumnName),
                transaction: Transaction);
            SetPkValue(pk, entity, id);
            return id;
        }

        /// <summary>
        /// Inserts an entity and immediately returns the generated identity value (async).
        /// </summary>
        public async Task<long> InsertAndGetIdAsync<T>(T entity, CancellationToken ct = default) where T : class
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            ThrowIfDisposed();
            await EnsureConnectionOpenAsync(ct).ConfigureAwait(false);

            var mapping = MappingCache.GetMapping<T>();
            var (sql, parameters) = SqlGenerator.GenerateInsert(mapping, entity, _dialect);
            LogSql(sql, parameters);
            await Connection.ExecuteAsync(new CommandDefinition(
                sql, (object)ToDynamicParameters(parameters),
                transaction: Transaction, commandTimeout: CommandTimeout,
                cancellationToken: ct)).ConfigureAwait(false);

            var pk = mapping.PrimaryKeys.FirstOrDefault(p => p.IsDbGenerated);
            var id = await Connection.ExecuteScalarAsync<long>(
                new CommandDefinition(_dialect.GetLastInsertIdSql(mapping.TableName, pk?.ColumnName),
                    transaction: Transaction, cancellationToken: ct)).ConfigureAwait(false);
            SetPkValue(pk, entity, id);
            return id;
        }

        #endregion

        #region BulkInsert (Phase 8.1)

        /// <summary>
        /// Inserts multiple entities directly using batched INSERT VALUES.
        /// Does not go through ChangeTracker or SubmitChanges.
        /// Bypasses identity retrieval for maximum throughput.
        /// </summary>
        public void BulkInsert<T>(IEnumerable<T> entities, int batchSize = 500) where T : class
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));
            ThrowIfDisposed();
            EnsureConnectionOpen();

            var mapping = MappingCache.GetMapping<T>();
            var columns = mapping.InsertableColumns;
            var columnNames = string.Join(", ", columns.Select(c => _dialect.QuoteIdentifier(c.ColumnName)));
            var tableName = SqlGenerator.QuoteTableName(mapping.TableName, _dialect);

            var ownTx = Transaction == null;
            var tx = Transaction ?? Connection.BeginTransaction();
            try
            {
                foreach (var batch in Batch(entities, batchSize))
                {
                    var dp = new DynamicParameters();
                    var valueRows = new List<string>();
                    int idx = 0;
                    foreach (var entity in batch)
                    {
                        var paramNames = new List<string>();
                        foreach (var col in columns)
                        {
                            var paramName = $"@b{idx}_{col.ColumnName}";
                            paramNames.Add(paramName);
                            dp.Add(paramName, col.Property.GetValue(entity));
                        }
                        valueRows.Add($"({string.Join(", ", paramNames)})");
                        idx++;
                    }

                    var sql = $"INSERT INTO {tableName} ({columnNames}) VALUES {string.Join(", ", valueRows)}";
                    LogSql(sql, null);
                    Connection.Execute(sql, (object)dp, transaction: tx, commandTimeout: CommandTimeout);
                }
                if (ownTx) tx.Commit();
            }
            catch { if (ownTx) tx.Rollback(); throw; }
            finally { if (ownTx) tx.Dispose(); }
        }

        /// <summary>
        /// Async version of BulkInsert.
        /// </summary>
        public async Task BulkInsertAsync<T>(IEnumerable<T> entities, int batchSize = 500,
            CancellationToken ct = default) where T : class
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));
            ThrowIfDisposed();
            await EnsureConnectionOpenAsync(ct).ConfigureAwait(false);

            var mapping = MappingCache.GetMapping<T>();
            var columns = mapping.InsertableColumns;
            var columnNames = string.Join(", ", columns.Select(c => _dialect.QuoteIdentifier(c.ColumnName)));
            var tableName = SqlGenerator.QuoteTableName(mapping.TableName, _dialect);

            var ownTx = Transaction == null;
            var tx = Transaction ?? Connection.BeginTransaction();
            try
            {
                foreach (var batch in Batch(entities, batchSize))
                {
                    var dp = new DynamicParameters();
                    var valueRows = new List<string>();
                    int idx = 0;
                    foreach (var entity in batch)
                    {
                        var paramNames = new List<string>();
                        foreach (var col in columns)
                        {
                            var paramName = $"@b{idx}_{col.ColumnName}";
                            paramNames.Add(paramName);
                            dp.Add(paramName, col.Property.GetValue(entity));
                        }
                        valueRows.Add($"({string.Join(", ", paramNames)})");
                        idx++;
                    }

                    var sql = $"INSERT INTO {tableName} ({columnNames}) VALUES {string.Join(", ", valueRows)}";
                    LogSql(sql, null);
                    await Connection.ExecuteAsync(new CommandDefinition(
                        sql, (object)dp, transaction: tx, commandTimeout: CommandTimeout,
                        cancellationToken: ct)).ConfigureAwait(false);
                }
                if (ownTx) tx.Commit();
            }
            catch { if (ownTx) tx.Rollback(); throw; }
            finally { if (ownTx) tx.Dispose(); }
        }

        /// <summary>
        /// Inserts or updates an entity based on primary key existence.
        /// SQLite: INSERT OR REPLACE. SQL Server: MERGE.
        /// </summary>
        public void Upsert<T>(T entity) where T : class
        {
            ThrowIfDisposed();
            var mapping = MappingCache.GetMapping<T>();
            var sql = SqlGenerator.GenerateUpsert(mapping, entity, _dialect);
            if (sql == null) throw new InvalidOperationException("Upsert requires at least one primary key column.");

            EnsureConnectionOpen();
            Connection.Execute(sql.Value.sql, (object)ToDynamicParameters(sql.Value.parameters),
                transaction: Transaction, commandTimeout: CommandTimeout);
        }

        /// <summary>
        /// Async version of Upsert.
        /// </summary>
        public async Task UpsertAsync<T>(T entity, CancellationToken ct = default) where T : class
        {
            ThrowIfDisposed();
            var mapping = MappingCache.GetMapping<T>();
            var sql = SqlGenerator.GenerateUpsert(mapping, entity, _dialect);
            if (sql == null) throw new InvalidOperationException("Upsert requires at least one primary key column.");

            await EnsureConnectionOpenAsync(ct).ConfigureAwait(false);
            await Connection.ExecuteAsync(new CommandDefinition(
                sql.Value.sql, ToDynamicParameters(sql.Value.parameters),
                transaction: Transaction, commandTimeout: CommandTimeout,
                cancellationToken: ct)).ConfigureAwait(false);
        }

        private static IEnumerable<List<T>> Batch<T>(IEnumerable<T> source, int size)
        {
            var batch = new List<T>(size);
            foreach (var item in source)
            {
                batch.Add(item);
                if (batch.Count >= size)
                {
                    yield return batch;
                    batch = new List<T>(size);
                }
            }
            if (batch.Count > 0) yield return batch;
        }

        #endregion

        #region Graph Insert (parent + child collections)

        /// <summary>
        /// Inserts an entity together with its one-to-many child collections in a single
        /// transaction. The parent is inserted first; its generated key is propagated to each
        /// child's foreign-key property; then children are inserted (recursively for nested graphs).
        /// </summary>
        public void InsertGraph<T>(T entity) where T : class
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            ThrowIfDisposed();
            EnsureConnectionOpen();

            var ownTx = Transaction == null;
            var tx = Transaction ?? Connection.BeginTransaction();
            var prevTx = Transaction;
            try
            {
                Transaction = tx;
                InsertGraphNode(entity, entity.GetType());
                if (ownTx) tx.Commit();
            }
            catch { if (ownTx) tx.Rollback(); throw; }
            finally
            {
                Transaction = prevTx;
                if (ownTx) tx.Dispose();
            }
        }

        /// <summary>
        /// Async version of InsertGraph.
        /// </summary>
        public async Task InsertGraphAsync<T>(T entity, CancellationToken ct = default) where T : class
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            ThrowIfDisposed();
            await EnsureConnectionOpenAsync(ct).ConfigureAwait(false);

            var ownTx = Transaction == null;
            var tx = Transaction ?? Connection.BeginTransaction();
            var prevTx = Transaction;
            try
            {
                Transaction = tx;
                await InsertGraphNodeAsync(entity, entity.GetType(), ct).ConfigureAwait(false);
                if (ownTx) tx.Commit();
            }
            catch { if (ownTx) tx.Rollback(); throw; }
            finally
            {
                Transaction = prevTx;
                if (ownTx) tx.Dispose();
            }
        }

        private void InsertGraphNode(object entity, Type entityType)
        {
            var mapping = MappingCache.GetMapping(entityType);

            // Insert the parent and capture its generated key (if any).
            var (sql, parameters) = SqlGenerator.GenerateInsert(mapping, entity, _dialect);
            LogSql(sql, parameters);
            Connection.Execute(sql, (object)ToDynamicParameters(parameters),
                transaction: Transaction, commandTimeout: CommandTimeout);

            var pk = mapping.PrimaryKeys.FirstOrDefault(p => p.IsDbGenerated);
            object pkValue;
            if (pk != null)
            {
                var id = Connection.ExecuteScalar<long>(
                    _dialect.GetLastInsertIdSql(mapping.TableName, pk.ColumnName), transaction: Transaction);
                SetPkValue(pk, entity, id);
                pkValue = pk.Property.GetValue(entity);
            }
            else
            {
                pkValue = mapping.PrimaryKeys.FirstOrDefault()?.Property.GetValue(entity);
            }

            InsertChildCollections(entity, mapping, pkValue);
        }

        private async Task InsertGraphNodeAsync(object entity, Type entityType, CancellationToken ct)
        {
            var mapping = MappingCache.GetMapping(entityType);

            var (sql, parameters) = SqlGenerator.GenerateInsert(mapping, entity, _dialect);
            LogSql(sql, parameters);
            await Connection.ExecuteAsync(new CommandDefinition(
                sql, (object)ToDynamicParameters(parameters),
                transaction: Transaction, commandTimeout: CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);

            var pk = mapping.PrimaryKeys.FirstOrDefault(p => p.IsDbGenerated);
            object pkValue;
            if (pk != null)
            {
                var id = await Connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    _dialect.GetLastInsertIdSql(mapping.TableName, pk.ColumnName),
                    transaction: Transaction, cancellationToken: ct)).ConfigureAwait(false);
                SetPkValue(pk, entity, id);
                pkValue = pk.Property.GetValue(entity);
            }
            else
            {
                pkValue = mapping.PrimaryKeys.FirstOrDefault()?.Property.GetValue(entity);
            }

            await InsertChildCollectionsAsync(entity, mapping, pkValue, ct).ConfigureAwait(false);
        }

        private void InsertChildCollections(object parent, EntityMapping mapping, object parentKey)
        {
            if (mapping.CollectionAssociations == null) return;

            foreach (var assoc in mapping.CollectionAssociations)
            {
                var collection = assoc.Property.GetValue(parent) as System.Collections.IEnumerable;
                if (collection == null) continue;

                var childType = assoc.OtherType;
                var childMapping = MappingCache.GetMapping(childType);
                // FK property on the child that references the parent's key.
                var fkProp = childType.GetProperty(assoc.OtherKey);

                foreach (var child in collection)
                {
                    if (child == null) continue;
                    if (fkProp != null && parentKey != null)
                        fkProp.SetValue(child, ConvertTo(parentKey, fkProp.PropertyType));
                    // Recurse so grandchildren are handled too.
                    InsertGraphNode(child, childType);
                }
            }
        }

        private async Task InsertChildCollectionsAsync(object parent, EntityMapping mapping, object parentKey, CancellationToken ct)
        {
            if (mapping.CollectionAssociations == null) return;

            foreach (var assoc in mapping.CollectionAssociations)
            {
                var collection = assoc.Property.GetValue(parent) as System.Collections.IEnumerable;
                if (collection == null) continue;

                var childType = assoc.OtherType;
                var fkProp = childType.GetProperty(assoc.OtherKey);

                foreach (var child in collection)
                {
                    if (child == null) continue;
                    if (fkProp != null && parentKey != null)
                        fkProp.SetValue(child, ConvertTo(parentKey, fkProp.PropertyType));
                    await InsertGraphNodeAsync(child, childType, ct).ConfigureAwait(false);
                }
            }
        }

        private static object ConvertTo(object value, Type targetType)
        {
            if (value == null) return null;
            var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (t.IsInstanceOfType(value)) return value;
            return Convert.ChangeType(value, t);
        }

        #endregion

        #region Change Processing

        private void ProcessChanges(IReadOnlyList<TrackedEntity> changes, IDbTransaction tx)
        {
            foreach (var tracked in changes)
            {
                var mapping = MappingCache.GetMapping(tracked.EntityType);
                Hooks.InvokeBeforeSave(tracked.Entity, tracked.EntityType, tracked.State);

                switch (tracked.State)
                {
                    case EntityState.Insert:
                        ExecuteCrud(mapping, tracked.Entity, tx, (m, e) => SqlGenerator.GenerateInsert(m, e, _dialect));
                        SetAutoGeneratedId(mapping, tracked.Entity, tx);
                        break;
                    case EntityState.Update:
                        if (tracked.ChangedProperties != null)
                        {
                            var (sql, parameters) = SqlGenerator.GeneratePartialUpdate(
                                mapping, tracked.Entity, tracked.ChangedProperties, _dialect);
                            if (sql != null)
                            {
                                LogSql(sql, parameters);
                                var affected = Connection.Execute(sql, (object)ToDynamicParameters(parameters),
                                    transaction: tx, commandTimeout: CommandTimeout);
                                CheckConcurrency(mapping, tracked.Entity, affected);
                            }
                        }
                        else
                        {
                            var affected = ExecuteCrud(mapping, tracked.Entity, tx, (m, e) => SqlGenerator.GenerateUpdate(m, e, _dialect));
                            CheckConcurrency(mapping, tracked.Entity, affected);
                        }
                        break;
                    case EntityState.Delete:
                        ExecuteCrud(mapping, tracked.Entity, tx, (m, e) => SqlGenerator.GenerateDelete(m, e, _dialect));
                        break;
                }

                Hooks.InvokeAfterSave(tracked.Entity, tracked.EntityType, tracked.State);
            }
        }

        private async Task ProcessChangesAsync(IReadOnlyList<TrackedEntity> changes, IDbTransaction tx, CancellationToken ct)
        {
            foreach (var tracked in changes)
            {
                var mapping = MappingCache.GetMapping(tracked.EntityType);
                switch (tracked.State)
                {
                    case EntityState.Insert:
                        await ExecuteCrudAsync(mapping, tracked.Entity, tx, (m, e) => SqlGenerator.GenerateInsert(m, e, _dialect), ct).ConfigureAwait(false);
                        await SetAutoGeneratedIdAsync(mapping, tracked.Entity, tx, ct).ConfigureAwait(false);
                        break;
                    case EntityState.Update:
                        if (tracked.ChangedProperties != null)
                        {
                            var (sql, parameters) = SqlGenerator.GeneratePartialUpdate(
                                mapping, tracked.Entity, tracked.ChangedProperties, _dialect);
                            if (sql != null)
                            {
                                LogSql(sql, parameters);
                                var affected = await Connection.ExecuteAsync(new CommandDefinition(
                                    sql, (object)ToDynamicParameters(parameters),
                                    transaction: tx, commandTimeout: CommandTimeout,
                                    cancellationToken: ct)).ConfigureAwait(false);
                                CheckConcurrency(mapping, tracked.Entity, affected);
                            }
                        }
                        else
                        {
                            var affected = await ExecuteCrudAsync(mapping, tracked.Entity, tx, (m, e) => SqlGenerator.GenerateUpdate(m, e, _dialect), ct).ConfigureAwait(false);
                            CheckConcurrency(mapping, tracked.Entity, affected);
                        }
                        break;
                    case EntityState.Delete:
                        await ExecuteCrudAsync(mapping, tracked.Entity, tx, (m, e) => SqlGenerator.GenerateDelete(m, e, _dialect), ct).ConfigureAwait(false);
                        break;
                }
            }
        }

        private int ExecuteCrud(EntityMapping mapping, object entity, IDbTransaction tx,
            Func<EntityMapping, object, (string, IDictionary<string, object>)> generator)
        {
            var (sql, parameters) = generator(mapping, entity);
            ApplyConverters(parameters, mapping);
            LogSql(sql, parameters);
            return Connection.Execute(sql, (object)ToDynamicParameters(parameters),
                transaction: tx, commandTimeout: CommandTimeout);
        }

        private async Task<int> ExecuteCrudAsync(EntityMapping mapping, object entity, IDbTransaction tx,
            Func<EntityMapping, object, (string, IDictionary<string, object>)> generator, CancellationToken ct)
        {
            var (sql, parameters) = generator(mapping, entity);
            ApplyConverters(parameters, mapping);
            LogSql(sql, parameters);
            return await Connection.ExecuteAsync(new CommandDefinition(
                sql, (object)ToDynamicParameters(parameters),
                transaction: tx, commandTimeout: CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        }

        /// <summary>
        /// Throws <see cref="ConcurrencyException"/> if a versioned UPDATE affected no rows,
        /// indicating the row was changed or deleted by another process since it was loaded.
        /// </summary>
        private static void CheckConcurrency(EntityMapping mapping, object entity, int affectedRows)
        {
            if (affectedRows == 0 && mapping.VersionColumns != null && mapping.VersionColumns.Count > 0)
                throw new ConcurrencyException(entity, mapping.EntityType);
        }

        #endregion

        #region Identity

        private void SetAutoGeneratedId(EntityMapping mapping, object entity, IDbTransaction tx)
        {
            var pk = mapping.PrimaryKeys.FirstOrDefault(p => p.IsDbGenerated);
            if (pk == null) return;
            var id = Connection.ExecuteScalar<long>(_dialect.GetLastInsertIdSql(mapping.TableName, pk.ColumnName), transaction: tx);
            SetPkValue(pk, entity, id);
        }

        private async Task SetAutoGeneratedIdAsync(EntityMapping mapping, object entity, IDbTransaction tx, CancellationToken ct)
        {
            var pk = mapping.PrimaryKeys.FirstOrDefault(p => p.IsDbGenerated);
            if (pk == null) return;
            var id = await Connection.ExecuteScalarAsync<long>(
                new CommandDefinition(_dialect.GetLastInsertIdSql(mapping.TableName, pk.ColumnName), transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            SetPkValue(pk, entity, id);
        }

        private static void SetPkValue(ColumnMapping pk, object entity, long id)
        {
            if (id <= 0) return;
            var t = Nullable.GetUnderlyingType(pk.Property.PropertyType) ?? pk.Property.PropertyType;
            pk.Property.SetValue(entity, Convert.ChangeType(id, t));
        }

        #endregion

        #region Utilities

        private void DetectAllChanges()
        {
            foreach (var tableType in _tables.Keys)
            {
                var mapping = MappingCache.GetMapping(tableType);
                var updates = _changeTracker.DetectChanges(mapping);
                if (updates.Count > 0) _changeTracker.AddUpdates(updates);
            }
        }

        private (string sql, DynamicParameters dp) ConvertParams(string query, object[] parameters)
        {
            var (sql, dict) = SqlGenerator.ConvertPositionalParameters(query, parameters);
            LogSql(sql, dict);
            return (sql, ToDynamicParameters(dict));
        }

        internal DynamicParameters ToDynamicParameters(IDictionary<string, object> parameters)
        {
            var dp = new DynamicParameters();
            if (parameters != null)
                foreach (var kv in parameters) dp.Add(kv.Key, kv.Value);
            return dp;
        }

        /// <summary>
        /// Applies registered value converters to parameter values.
        /// Converts model types to database types (e.g., Enum → string).
        /// </summary>
        private void ApplyConverters(IDictionary<string, object> parameters, EntityMapping mapping)
        {
            if (parameters == null || !Converters.HasAny) return;

            var keys = new List<string>(parameters.Keys);
            foreach (var key in keys)
            {
                var value = parameters[key];
                if (value == null) continue;

                var valueType = value.GetType();
                if (Converters.HasConverter(valueType))
                {
                    parameters[key] = Converters.ConvertToDb(value, valueType);
                }
            }
        }

        internal void EnsureConnectionOpen()
        {
            if (Connection.State != ConnectionState.Open) Connection.Open();
        }

        internal async Task EnsureConnectionOpenAsync(CancellationToken ct = default)
        {
            if (Connection.State != ConnectionState.Open)
            {
                if (Connection is DbConnection dbConn)
                    await dbConn.OpenAsync(ct).ConfigureAwait(false);
                else
                    Connection.Open();
            }
        }

        private void LogSql(string sql, IDictionary<string, object> parameters)
        {
            if (Log == null) return;
            Log.WriteLine(sql);
            if (parameters?.Count > 0)
                foreach (var kv in parameters) Log.WriteLine($"  {kv.Key} = {kv.Value}");
            Log.WriteLine();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name, "DataContext accessed after Dispose.");
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Transaction?.Dispose();
            if (_ownsConnection) Connection?.Dispose();
        }

        #endregion
    }
}
