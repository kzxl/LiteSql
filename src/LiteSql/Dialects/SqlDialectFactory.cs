using System;
using System.Data;

namespace LiteSql.Dialects
{
    /// <summary>
    /// Factory for creating SQL dialect instances based on connection type or provider name.
    /// </summary>
    public static class SqlDialectFactory
    {
        /// <summary>
        /// Creates a dialect instance based on the connection type.
        /// </summary>
        public static ISqlDialect GetDialect(IDbConnection connection)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            var connectionType = connection.GetType().Name.ToLowerInvariant();

            // SQL Server
            if (connectionType.Contains("sqlconnection") || connectionType.Contains("sqlclient"))
                return new SqlServerDialect();

            // MySQL
            if (connectionType.Contains("mysqlconnection") || connectionType.Contains("mysql"))
                return new MySqlDialect();

            // PostgreSQL
            if (connectionType.Contains("npgsqlconnection") || connectionType.Contains("postgres"))
                return new PostgreSqlDialect();

            // SQLite
            if (connectionType.Contains("sqliteconnection") || connectionType.Contains("sqlite"))
                return new SqliteDialect();

            // Default to SQL Server for unknown types
            return new SqlServerDialect();
        }

        /// <summary>
        /// Creates a dialect instance based on provider name.
        /// </summary>
        public static ISqlDialect GetDialect(string providerName)
        {
            if (string.IsNullOrEmpty(providerName))
                throw new ArgumentNullException(nameof(providerName));

            var provider = providerName.ToLowerInvariant();

            if (provider.Contains("sqlserver") || provider.Contains("mssql"))
                return new SqlServerDialect();

            if (provider.Contains("mysql") || provider.Contains("mariadb"))
                return new MySqlDialect();

            if (provider.Contains("postgres") || provider.Contains("npgsql"))
                return new PostgreSqlDialect();

            if (provider.Contains("sqlite"))
                return new SqliteDialect();

            throw new NotSupportedException($"Provider '{providerName}' is not supported. Supported providers: SQL Server, MySQL, PostgreSQL, SQLite");
        }

        /// <summary>
        /// Gets all available dialects.
        /// </summary>
        public static ISqlDialect[] GetAllDialects()
        {
            return new ISqlDialect[]
            {
                new SqlServerDialect(),
                new MySqlDialect(),
                new PostgreSqlDialect(),
                new SqliteDialect()
            };
        }
    }
}
