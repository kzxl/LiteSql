using System;

namespace LiteSql.Dialects
{
    /// <summary>
    /// SQL Server dialect implementation.
    /// Supports SQL Server 2012+ (OFFSET/FETCH syntax).
    /// </summary>
    public class SqlServerDialect : ISqlDialect
    {
        public string ProviderName => "SQL Server";

        public string ParameterPrefix => "@";

        public bool SupportsReturningClause => false;

        public bool SupportsOutputClause => true;

        /// <summary>
        /// SQL Server uses square brackets for identifiers: [TableName]
        /// </summary>
        public string QuoteIdentifier(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
                throw new ArgumentNullException(nameof(identifier));

            // Escape existing brackets
            identifier = identifier.Replace("]", "]]");
            return $"[{identifier}]";
        }

        /// <summary>
        /// SQL Server 2012+ uses OFFSET/FETCH syntax
        /// </summary>
        public string GetLimitClause(int? skip, int? take)
        {
            if (!skip.HasValue && !take.HasValue)
                return string.Empty;

            // SQL Server requires ORDER BY before OFFSET/FETCH
            var clause = string.Empty;

            if (skip.HasValue && skip.Value > 0)
            {
                clause = $"OFFSET {skip.Value} ROWS";

                if (take.HasValue && take.Value > 0)
                    clause += $" FETCH NEXT {take.Value} ROWS ONLY";
            }
            else if (take.HasValue && take.Value > 0)
            {
                // If only TAKE, use OFFSET 0
                clause = $"OFFSET 0 ROWS FETCH NEXT {take.Value} ROWS ONLY";
            }

            return clause;
        }

        /// <summary>
        /// SQL Server uses SCOPE_IDENTITY() to get last inserted ID
        /// </summary>
        public string GetLastInsertIdSql(string tableName, string columnName)
        {
            return "SELECT CAST(SCOPE_IDENTITY() AS INT)";
        }

        /// <summary>
        /// SQL Server uses IDENTITY for auto-increment
        /// </summary>
        public string GetAutoIncrementSql()
        {
            return "IDENTITY(1,1)";
        }

        public string GetTableExistsSql(string tableName)
        {
            return $"SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{EscapeStringValue(tableName)}') THEN 1 ELSE 0 END";
        }

        public string GetDbType(Type clrType)
        {
            if (clrType == typeof(int)) return "INT";
            if (clrType == typeof(long)) return "BIGINT";
            if (clrType == typeof(short)) return "SMALLINT";
            if (clrType == typeof(byte)) return "TINYINT";
            if (clrType == typeof(bool)) return "BIT";
            if (clrType == typeof(decimal)) return "DECIMAL(18,2)";
            if (clrType == typeof(double)) return "FLOAT";
            if (clrType == typeof(float)) return "REAL";
            if (clrType == typeof(string)) return "NVARCHAR(MAX)";
            if (clrType == typeof(DateTime)) return "DATETIME2";
            if (clrType == typeof(DateTimeOffset)) return "DATETIMEOFFSET";
            if (clrType == typeof(Guid)) return "UNIQUEIDENTIFIER";
            if (clrType == typeof(byte[])) return "VARBINARY(MAX)";

            // Handle nullable types
            var underlyingType = Nullable.GetUnderlyingType(clrType);
            if (underlyingType != null)
                return GetDbType(underlyingType);

            return "NVARCHAR(MAX)"; // Default fallback
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
