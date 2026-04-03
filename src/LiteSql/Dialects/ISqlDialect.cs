using System;

namespace LiteSql.Dialects
{
    /// <summary>
    /// Defines SQL dialect-specific behavior for different database providers.
    /// Allows LiteSql to support multiple databases (SQL Server, MySQL, PostgreSQL, etc.)
    /// </summary>
    public interface ISqlDialect
    {
        /// <summary>
        /// Gets the name of the database provider (e.g., "SQL Server", "MySQL", "PostgreSQL")
        /// </summary>
        string ProviderName { get; }

        /// <summary>
        /// Quotes an identifier (table name, column name) according to database rules.
        /// SQL Server: [Name], MySQL: `Name`, PostgreSQL: "Name"
        /// </summary>
        string QuoteIdentifier(string identifier);

        /// <summary>
        /// Generates LIMIT/OFFSET clause for pagination.
        /// SQL Server: OFFSET x ROWS FETCH NEXT y ROWS ONLY
        /// MySQL/PostgreSQL: LIMIT y OFFSET x
        /// </summary>
        string GetLimitClause(int? skip, int? take);

        /// <summary>
        /// Gets the SQL for retrieving the last inserted identity/auto-increment value.
        /// SQL Server: SELECT SCOPE_IDENTITY()
        /// MySQL: SELECT LAST_INSERT_ID()
        /// PostgreSQL: RETURNING id
        /// </summary>
        string GetLastInsertIdSql(string tableName, string columnName);

        /// <summary>
        /// Gets the parameter prefix for this database.
        /// SQL Server/MySQL: @
        /// PostgreSQL: $
        /// Oracle: :
        /// </summary>
        string ParameterPrefix { get; }

        /// <summary>
        /// Checks if the database supports RETURNING clause (PostgreSQL).
        /// </summary>
        bool SupportsReturningClause { get; }

        /// <summary>
        /// Checks if the database supports OUTPUT clause (SQL Server).
        /// </summary>
        bool SupportsOutputClause { get; }

        /// <summary>
        /// Gets the SQL for checking if a table exists.
        /// </summary>
        string GetTableExistsSql(string tableName);

        /// <summary>
        /// Converts a CLR type to database-specific type name.
        /// </summary>
        string GetDbType(Type clrType);

        /// <summary>
        /// Gets the SQL for creating an auto-increment/identity column.
        /// SQL Server: IDENTITY(1,1)
        /// MySQL: AUTO_INCREMENT
        /// PostgreSQL: SERIAL or GENERATED ALWAYS AS IDENTITY
        /// </summary>
        string GetAutoIncrementSql();

        /// <summary>
        /// Escapes a string value for SQL (prevents SQL injection in dynamic SQL).
        /// </summary>
        string EscapeStringValue(string value);
    }
}
