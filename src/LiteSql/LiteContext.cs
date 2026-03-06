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

namespace LiteSql
{
    /// <summary>
    /// Lightweight DataContext replacement for .NET Core, backed by Dapper.
    /// Compatible with System.Data.Linq.DataContext API surface.
    /// </summary>
    public class LiteContext : IDisposable
    {
        private readonly bool _ownsConnection;
        private readonly ConcurrentDictionary<Type, object> _tables
            = new ConcurrentDictionary<Type, object>();
        private readonly ChangeTracker _changeTracker = new ChangeTracker();
        private bool _disposed;

        #region Constructors

        /// <summary>
        /// Initializes a new LiteContext with an existing connection.
        /// The connection will NOT be disposed by this context.
        /// </summary>
        public LiteContext(IDbConnection connection)
        {
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _ownsConnection = false;
        }

        /// <summary>
        /// Initializes a new LiteContext with a connection string.
        /// A new SqlConnection will be created and owned by this context.
        /// The caller must set ConnectionFactory before using this constructor.
        /// </summary>
        public LiteContext(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentNullException(nameof(connectionString));

            Connection = ConnectionFactory?.Invoke(connectionString)
                ?? throw new InvalidOperationException(
                    "LiteContext.ConnectionFactory must be set before using the string constructor. " +
                    "Example: LiteContext.ConnectionFactory = cs => new SqlConnection(cs);"
                );
            _ownsConnection = true;
        }

        #endregion

        #region Static Configuration

        /// <summary>
        /// Factory function to create IDbConnection from a connection string.
        /// Must be set once at app startup if using the string constructor.
        /// Example: LiteContext.ConnectionFactory = cs => new SqlConnection(cs);
        /// </summary>
        public static Func<string, IDbConnection> ConnectionFactory { get; set; }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the database connection used by this context.
        /// Compatible with DataContext.Connection.
        /// </summary>
        public IDbConnection Connection { get; }

        /// <summary>
        /// Gets or sets the active transaction.
        /// Compatible with DataContext.Transaction.
        /// </summary>
        public IDbTransaction Transaction { get; set; }

        /// <summary>
        /// Gets or sets the command timeout in seconds.
        /// Compatible with DataContext.CommandTimeout.
        /// </summary>
        public int CommandTimeout { get; set; } = 30;

        /// <summary>
        /// Gets or sets the log destination for SQL queries.
        /// Compatible with DataContext.Log.
        /// </summary>
        public TextWriter Log { get; set; }

        /// <summary>
        /// Gets or sets whether object tracking is enabled.
        /// Compatible with DataContext.ObjectTrackingEnabled.
        /// </summary>
        public bool ObjectTrackingEnabled { get; set; } = true;

        #endregion

        #region Core Methods

        /// <summary>
        /// Returns a Table&lt;T&gt; for the specified entity type.
        /// Compatible with DataContext.GetTable&lt;T&gt;().
        /// </summary>
        public Table<T> GetTable<T>() where T : class
        {
            ThrowIfDisposed();
            return (Table<T>)_tables.GetOrAdd(typeof(T), _ => new Table<T>(this, _changeTracker));
        }

        /// <summary>
        /// Submits all pending changes (inserts and deletes) to the database.
        /// Compatible with DataContext.SubmitChanges().
        /// </summary>
        public void SubmitChanges()
        {
            ThrowIfDisposed();

            // Detect updated entities from snapshots before getting pending changes
            DetectAllChanges();

            var changes = _changeTracker.GetPendingChanges();
            if (changes.Count == 0) return;

            EnsureConnectionOpen();

            // Use existing transaction or create a new one
            var ownTransaction = Transaction == null;
            var transaction = Transaction ?? Connection.BeginTransaction();

            try
            {
                foreach (var tracked in changes)
                {
                    var mapping = MappingCache.GetMapping(tracked.EntityType);

                    switch (tracked.State)
                    {
                        case EntityState.Insert:
                            ExecuteInsert(mapping, tracked.Entity, transaction);
                            break;

                        case EntityState.Update:
                            ExecuteUpdate(mapping, tracked.Entity, transaction);
                            break;

                        case EntityState.Delete:
                            ExecuteDelete(mapping, tracked.Entity, transaction);
                            break;
                    }
                }

                if (ownTransaction)
                    transaction.Commit();

                _changeTracker.AcceptChanges();
            }
            catch
            {
                if (ownTransaction)
                    transaction.Rollback();
                throw;
            }
            finally
            {
                if (ownTransaction)
                    transaction.Dispose();
            }
        }

        /// <summary>
        /// Executes a raw SQL query and maps results to type T via Dapper.
        /// Compatible with DataContext.ExecuteQuery&lt;T&gt;(string, params object[]).
        /// Supports L2S-style positional parameters: {0}, {1}, etc.
        /// </summary>
        public IEnumerable<T> ExecuteQuery<T>(string query, params object[] parameters)
        {
            ThrowIfDisposed();
            EnsureConnectionOpen();

            var (sql, dapperParams) = SqlGenerator.ConvertPositionalParameters(query, parameters);

            LogSql(sql, dapperParams);

            return Connection.Query<T>(sql, (object)ToDynamicParameters(dapperParams),
                transaction: Transaction, commandTimeout: CommandTimeout);
        }

        /// <summary>
        /// Executes a raw SQL command (INSERT, UPDATE, DELETE) and returns affected row count.
        /// Compatible with DataContext.ExecuteCommand(string, params object[]).
        /// Supports L2S-style positional parameters: {0}, {1}, etc.
        /// </summary>
        public int ExecuteCommand(string command, params object[] parameters)
        {
            ThrowIfDisposed();
            EnsureConnectionOpen();

            var (sql, dapperParams) = SqlGenerator.ConvertPositionalParameters(command, parameters);

            LogSql(sql, dapperParams);

            return Connection.Execute(sql, (object)ToDynamicParameters(dapperParams),
                transaction: Transaction, commandTimeout: CommandTimeout);
        }

        #endregion

        #region Private Helpers

        private void ExecuteInsert(EntityMapping mapping, object entity, IDbTransaction transaction)
        {
            var (sql, parameters) = SqlGenerator.GenerateInsert(mapping, entity);

            LogSql(sql, parameters);

            // Execute INSERT statement
            Connection.Execute(sql, (object)ToDynamicParameters(parameters),
                transaction: transaction, commandTimeout: CommandTimeout);

            // If there's an auto-generated PK, query for the last inserted ID
            var autoGenPk = mapping.PrimaryKeys.FirstOrDefault(pk => pk.IsDbGenerated);
            if (autoGenPk != null)
            {
                // Auto-detect identity query based on connection type
                var identitySql = GetLastInsertIdSql(Connection);
                var id = Connection.ExecuteScalar<long>(identitySql, transaction: transaction);

                // Set the generated ID back on the entity
                if (id > 0)
                {
                    var targetType = Nullable.GetUnderlyingType(autoGenPk.Property.PropertyType)
                        ?? autoGenPk.Property.PropertyType;
                    autoGenPk.Property.SetValue(entity, Convert.ChangeType(id, targetType));
                }
            }
        }

        /// <summary>
        /// Returns the SQL to retrieve the last inserted identity value,
        /// based on the connection type (SQLite vs SQL Server).
        /// </summary>
        private static string GetLastInsertIdSql(IDbConnection connection)
        {
            var typeName = connection.GetType().Name.ToLowerInvariant();
            if (typeName.Contains("sqlite"))
                return "SELECT last_insert_rowid()";

            // Default to SQL Server
            return "SELECT CAST(SCOPE_IDENTITY() AS BIGINT)";
        }

        private void ExecuteDelete(EntityMapping mapping, object entity, IDbTransaction transaction)
        {
            var (sql, parameters) = SqlGenerator.GenerateDelete(mapping, entity);

            LogSql(sql, parameters);

            Connection.Execute(sql, (object)ToDynamicParameters(parameters),
                transaction: transaction, commandTimeout: CommandTimeout);
        }

        private void ExecuteUpdate(EntityMapping mapping, object entity, IDbTransaction transaction)
        {
            var (sql, parameters) = SqlGenerator.GenerateUpdate(mapping, entity);

            LogSql(sql, parameters);

            Connection.Execute(sql, (object)ToDynamicParameters(parameters),
                transaction: transaction, commandTimeout: CommandTimeout);
        }

        /// <summary>
        /// Detects changes across all tracked entity types by comparing
        /// current property values against original snapshots.
        /// </summary>
        private void DetectAllChanges()
        {
            // Get all unique entity types from the table cache
            foreach (var tableType in _tables.Keys)
            {
                var mapping = MappingCache.GetMapping(tableType);
                var updates = _changeTracker.DetectChanges(mapping);
                if (updates.Count > 0)
                    _changeTracker.AddUpdates(updates);
            }
        }

        private DynamicParameters ToDynamicParameters(IDictionary<string, object> parameters)
        {
            var dp = new DynamicParameters();
            if (parameters != null)
            {
                foreach (var kv in parameters)
                {
                    dp.Add(kv.Key, kv.Value);
                }
            }
            return dp;
        }

        internal void EnsureConnectionOpen()
        {
            if (Connection.State != ConnectionState.Open)
            {
                Connection.Open();
            }
        }

        private void LogSql(string sql, IDictionary<string, object> parameters)
        {
            if (Log == null) return;

            Log.WriteLine(sql);
            if (parameters != null && parameters.Count > 0)
            {
                foreach (var kv in parameters)
                {
                    Log.WriteLine($"  {kv.Key} = {kv.Value}");
                }
            }
            Log.WriteLine();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name,
                    "DataContext accessed after Dispose.");
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Transaction?.Dispose();

            if (_ownsConnection)
            {
                Connection?.Dispose();
            }
        }

        #endregion
    }
}
