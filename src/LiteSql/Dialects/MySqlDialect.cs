using System;

namespace LiteSql.Dialects
{
    /// <summary>
    /// MySQL dialect implementation.
    /// Supports MySQL 5.7+ and MariaDB 10.2+.
    /// </summary>
    public class MySqlDialect : ISqlDialect
    {
        public string ProviderName => "MySQL";

        public string ParameterPrefix => "@";

        public bool SupportsReturningClause => false;

        public bool SupportsOutputClause => false;

        /// <summary>
        /// MySQL uses backticks for identifiers: `TableName`
        /// </summary>
        public string QuoteIdentifier(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
                throw new ArgumentNullException(nameof(identifier));

            // Escape existing backticks
            identifier = identifier.Replace("`", "``");
            return $"`{identifier}`";
        }

        /// <summary>
        /// MySQL uses LIMIT/OFFSET syntax
        /// </summary>
        public string GetLimitClause(int? skip, int? take)
        {
            if (!skip.HasValue && !take.HasValue)
                return string.Empty;

            var clause = string.Empty;

            if (take.HasValue && take.Value > 0)
            {
                clause = $"LIMIT {take.Value}";

                if (skip.HasValue && skip.Value > 0)
                    clause += $" OFFSET {skip.Value}";
            }
            else if (skip.HasValue && skip.Value > 0)
            {
                // MySQL requires LIMIT when using OFFSET
                // Use a very large number as workaround
                clause = $"LIMIT 18446744073709551615 OFFSET {skip.Value}";
            }

            return clause;
        }

        /// <summary>
        /// MySQL uses LAST_INSERT_ID() to get last inserted ID
        /// </summary>
        public string GetLastInsertIdSql(string tableName, string columnName)
        {
            return "SELECT LAST_INSERT_ID()";
        }

        /// <summary>
        /// MySQL uses AUTO_INCREMENT for auto-increment
        /// </summary>
        public string GetAutoIncrementSql()
        {
            return "AUTO_INCREMENT";
        }

        public string GetTableExistsSql(string tableName)
        {
            return $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{EscapeStringValue(tableName)}'";
        }

        public string GetDbType(Type clrType)
        {
            if (clrType == typeof(int)) return "INT";
            if (clrType == typeof(long)) return "BIGINT";
            if (clrType == typeof(short)) return "SMALLINT";
            if (clrType == typeof(byte)) return "TINYINT";
            if (clrType == typeof(bool)) return "TINYINT(1)";
            if (clrType == typeof(decimal)) return "DECIMAL(18,2)";
            if (clrType == typeof(double)) return "DOUBLE";
            if (clrType == typeof(float)) return "FLOAT";
            if (clrType == typeof(string)) return "TEXT";
            if (clrType == typeof(DateTime)) return "DATETIME";
            if (clrType == typeof(DateTimeOffset)) return "DATETIME"; // MySQL doesn't have native DateTimeOffset
            if (clrType == typeof(Guid)) return "CHAR(36)";
            if (clrType == typeof(byte[])) return "LONGBLOB";

            // Handle nullable types
            var underlyingType = Nullable.GetUnderlyingType(clrType);
            if (underlyingType != null)
                return GetDbType(underlyingType);

            return "TEXT"; // Default fallback
        }

        public string EscapeStringValue(string value)
        {
            if (value == null)
                return "NULL";

            // Escape single quotes by doubling them
            return value.Replace("'", "''");
        }
    }
}
