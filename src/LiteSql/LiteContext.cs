using Dapper;
using LiteSql.ChangeTracking;
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

        #region Constructors

        public LiteContext(IDbConnection connection)
        {
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
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
            var (sql, parameters) = SqlGenerator.GenerateInsert(mapping, entity);
            LogSql(sql, parameters);
            Connection.Execute(sql, (object)ToDynamicParameters(parameters),
                transaction: Transaction, commandTimeout: CommandTimeout);

            var id = Connection.ExecuteScalar<long>(GetLastInsertIdSql(Connection),
                transaction: Transaction);
            SetPkValue(mapping.PrimaryKeys.FirstOrDefault(p => p.IsDbGenerated), entity, id);
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
            var (sql, parameters) = SqlGenerator.GenerateInsert(mapping, entity);
            LogSql(sql, parameters);
            await Connection.ExecuteAsync(new CommandDefinition(
                sql, (object)ToDynamicParameters(parameters),
                transaction: Transaction, commandTimeout: CommandTimeout,
                cancellationToken: ct)).ConfigureAwait(false);

            var id = await Connection.ExecuteScalarAsync<long>(
                new CommandDefinition(GetLastInsertIdSql(Connection),
                    transaction: Transaction, cancellationToken: ct)).ConfigureAwait(false);
            SetPkValue(mapping.PrimaryKeys.FirstOrDefault(p => p.IsDbGenerated), entity, id);
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
            var columnNames = string.Join(", ", columns.Select(c => $"[{c.ColumnName}]"));
            var tableName = SqlGenerator.QuoteTableName(mapping.TableName);

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
            var columnNames = string.Join(", ", columns.Select(c => $"[{c.ColumnName}]"));
            var tableName = SqlGenerator.QuoteTableName(mapping.TableName);

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

        #region Change Processing

        private void ProcessChanges(IReadOnlyList<TrackedEntity> changes, IDbTransaction tx)
        {
            foreach (var tracked in changes)
            {
                var mapping = MappingCache.GetMapping(tracked.EntityType);
                switch (tracked.State)
                {
                    case EntityState.Insert:
                        ExecuteCrud(mapping, tracked.Entity, tx, SqlGenerator.GenerateInsert);
                        SetAutoGeneratedId(mapping, tracked.Entity, tx);
                        break;
                    case EntityState.Update:
                        if (tracked.ChangedProperties != null)
                        {
                            var (sql, parameters) = SqlGenerator.GeneratePartialUpdate(
                                mapping, tracked.Entity, tracked.ChangedProperties);
                            if (sql != null)
                            {
                                LogSql(sql, parameters);
                                Connection.Execute(sql, (object)ToDynamicParameters(parameters),
                                    transaction: tx, commandTimeout: CommandTimeout);
                            }
                        }
                        else
                        {
                            ExecuteCrud(mapping, tracked.Entity, tx, SqlGenerator.GenerateUpdate);
                        }
                        break;
                    case EntityState.Delete:
                        ExecuteCrud(mapping, tracked.Entity, tx, SqlGenerator.GenerateDelete);
                        break;
                }
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
                        await ExecuteCrudAsync(mapping, tracked.Entity, tx, SqlGenerator.GenerateInsert, ct).ConfigureAwait(false);
                        await SetAutoGeneratedIdAsync(mapping, tracked.Entity, tx, ct).ConfigureAwait(false);
                        break;
                    case EntityState.Update:
                        if (tracked.ChangedProperties != null)
                        {
                            var (sql, parameters) = SqlGenerator.GeneratePartialUpdate(
                                mapping, tracked.Entity, tracked.ChangedProperties);
                            if (sql != null)
                            {
                                LogSql(sql, parameters);
                                await Connection.ExecuteAsync(new CommandDefinition(
                                    sql, (object)ToDynamicParameters(parameters),
                                    transaction: tx, commandTimeout: CommandTimeout,
                                    cancellationToken: ct)).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            await ExecuteCrudAsync(mapping, tracked.Entity, tx, SqlGenerator.GenerateUpdate, ct).ConfigureAwait(false);
                        }
                        break;
                    case EntityState.Delete:
                        await ExecuteCrudAsync(mapping, tracked.Entity, tx, SqlGenerator.GenerateDelete, ct).ConfigureAwait(false);
                        break;
                }
            }
        }

        private void ExecuteCrud(EntityMapping mapping, object entity, IDbTransaction tx,
            Func<EntityMapping, object, (string, IDictionary<string, object>)> generator)
        {
            var (sql, parameters) = generator(mapping, entity);
            LogSql(sql, parameters);
            Connection.Execute(sql, (object)ToDynamicParameters(parameters),
                transaction: tx, commandTimeout: CommandTimeout);
        }

        private async Task ExecuteCrudAsync(EntityMapping mapping, object entity, IDbTransaction tx,
            Func<EntityMapping, object, (string, IDictionary<string, object>)> generator, CancellationToken ct)
        {
            var (sql, parameters) = generator(mapping, entity);
            LogSql(sql, parameters);
            await Connection.ExecuteAsync(new CommandDefinition(
                sql, (object)ToDynamicParameters(parameters),
                transaction: tx, commandTimeout: CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        }

        #endregion

        #region Identity

        private void SetAutoGeneratedId(EntityMapping mapping, object entity, IDbTransaction tx)
        {
            var pk = mapping.PrimaryKeys.FirstOrDefault(p => p.IsDbGenerated);
            if (pk == null) return;
            var id = Connection.ExecuteScalar<long>(GetLastInsertIdSql(Connection), transaction: tx);
            SetPkValue(pk, entity, id);
        }

        private async Task SetAutoGeneratedIdAsync(EntityMapping mapping, object entity, IDbTransaction tx, CancellationToken ct)
        {
            var pk = mapping.PrimaryKeys.FirstOrDefault(p => p.IsDbGenerated);
            if (pk == null) return;
            var id = await Connection.ExecuteScalarAsync<long>(
                new CommandDefinition(GetLastInsertIdSql(Connection), transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            SetPkValue(pk, entity, id);
        }

        private static void SetPkValue(ColumnMapping pk, object entity, long id)
        {
            if (id <= 0) return;
            var t = Nullable.GetUnderlyingType(pk.Property.PropertyType) ?? pk.Property.PropertyType;
            pk.Property.SetValue(entity, Convert.ChangeType(id, t));
        }

        internal static string GetLastInsertIdSql(IDbConnection connection)
        {
            if (connection.GetType().Name.IndexOf("sqlite", StringComparison.OrdinalIgnoreCase) >= 0)
                return "SELECT last_insert_rowid()";
            return "SELECT CAST(SCOPE_IDENTITY() AS BIGINT)";
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
