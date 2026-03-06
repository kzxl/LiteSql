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
                        ExecuteCrud(mapping, tracked.Entity, tx, SqlGenerator.GenerateUpdate);
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
                        await ExecuteCrudAsync(mapping, tracked.Entity, tx, SqlGenerator.GenerateUpdate, ct).ConfigureAwait(false);
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
