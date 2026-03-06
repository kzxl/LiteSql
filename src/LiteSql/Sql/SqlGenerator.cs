using LiteSql.ChangeTracking;
using LiteSql.Mapping;
using System;
using System.Collections.Generic;
using System.Data;
using System.Dynamic;
using System.Linq;

namespace LiteSql.Sql
{
    /// <summary>
    /// Generates parameterized SQL statements (INSERT, DELETE, SELECT, UPDATE) 
    /// from entity mapping metadata. SQL Server dialect by default.
    /// </summary>
    public static class SqlGenerator
    {
        /// <summary>
        /// Generates a SELECT * statement for the entity type.
        /// </summary>
        public static string GenerateSelectAll(EntityMapping mapping)
        {
            return $"SELECT * FROM {QuoteTableName(mapping.TableName)}";
        }

        /// <summary>
        /// Generates a parameterized INSERT statement and its parameter dictionary.
        /// Skips IsDbGenerated columns.
        /// Returns the generated identity value if any PK is IsDbGenerated.
        /// </summary>
        public static (string Sql, IDictionary<string, object> Parameters) GenerateInsert(
            EntityMapping mapping, object entity)
        {
            var columns = mapping.InsertableColumns;
            var columnNames = columns.Select(c => $"[{c.ColumnName}]");
            var paramNames = columns.Select(c => $"@{c.ColumnName}");

            var sql = $"INSERT INTO {QuoteTableName(mapping.TableName)} ({string.Join(", ", columnNames)}) " +
                      $"VALUES ({string.Join(", ", paramNames)})";

            // Identity retrieval is handled separately by LiteContext.ExecuteInsert
            // to support different DB backends (SQL Server, SQLite, etc.)

            var parameters = BuildParameters(columns, entity);
            return (sql, parameters);
        }

        /// <summary>
        /// Generates a parameterized DELETE statement using primary key(s).
        /// </summary>
        public static (string Sql, IDictionary<string, object> Parameters) GenerateDelete(
            EntityMapping mapping, object entity)
        {
            if (mapping.PrimaryKeys.Count == 0)
                throw new InvalidOperationException(
                    $"Cannot generate DELETE for '{mapping.EntityType.Name}': no primary key defined.");

            var whereClause = BuildWhereByPrimaryKeys(mapping, entity, out var parameters);
            var sql = $"DELETE FROM {QuoteTableName(mapping.TableName)} WHERE {whereClause}";
            return (sql, parameters);
        }

        /// <summary>
        /// Generates a parameterized UPDATE statement.
        /// Updates all non-PK, non-DbGenerated columns.
        /// </summary>
        public static (string Sql, IDictionary<string, object> Parameters) GenerateUpdate(
            EntityMapping mapping, object entity)
        {
            if (mapping.PrimaryKeys.Count == 0)
                throw new InvalidOperationException(
                    $"Cannot generate UPDATE for '{mapping.EntityType.Name}': no primary key defined.");

            if (mapping.UpdatableColumns.Count == 0)
                throw new InvalidOperationException(
                    $"Cannot generate UPDATE for '{mapping.EntityType.Name}': no updatable columns.");

            var setClauses = mapping.UpdatableColumns
                .Select(c => $"[{c.ColumnName}] = @{c.ColumnName}");

            var whereClause = BuildWhereByPrimaryKeys(mapping, entity, out var whereParams);

            var sql = $"UPDATE {QuoteTableName(mapping.TableName)} " +
                      $"SET {string.Join(", ", setClauses)} " +
                      $"WHERE {whereClause}";

            // Merge SET parameters with WHERE parameters
            var allParams = BuildParameters(mapping.UpdatableColumns, entity);
            foreach (var kv in whereParams)
            {
                allParams[kv.Key] = kv.Value;
            }

            return (sql, allParams);
        }

        /// <summary>
        /// Converts L2S-style positional parameters ({0}, {1}, ...) to named Dapper parameters.
        /// </summary>
        public static (string Sql, IDictionary<string, object> Parameters) ConvertPositionalParameters(
            string sql, object[] args)
        {
            if (args == null || args.Length == 0)
                return (sql, new Dictionary<string, object>());

            var parameters = new Dictionary<string, object>();
            for (int i = 0; i < args.Length; i++)
            {
                var paramName = $"@p{i}";
                sql = sql.Replace("{" + i + "}", paramName);
                parameters[paramName] = args[i];
            }

            return (sql, parameters);
        }

        #region Private Helpers

        private static string BuildWhereByPrimaryKeys(
            EntityMapping mapping, object entity, out IDictionary<string, object> parameters)
        {
            parameters = new Dictionary<string, object>();
            var clauses = new List<string>();

            foreach (var pk in mapping.PrimaryKeys)
            {
                var paramName = $"@pk_{pk.ColumnName}";
                clauses.Add($"[{pk.ColumnName}] = {paramName}");
                parameters[paramName] = pk.Property.GetValue(entity);
            }

            return string.Join(" AND ", clauses);
        }

        private static IDictionary<string, object> BuildParameters(
            IReadOnlyList<ColumnMapping> columns, object entity)
        {
            var parameters = new Dictionary<string, object>();
            foreach (var col in columns)
            {
                parameters[$"@{col.ColumnName}"] = col.Property.GetValue(entity);
            }
            return parameters;
        }

        #endregion

        /// <summary>
        /// Quotes a table name for SQL. Handles schema.table format:
        /// "dbo.Users" → "[dbo].[Users]", "Users" → "[Users]".
        /// </summary>
        internal static string QuoteTableName(string tableName)
        {
            if (string.IsNullOrEmpty(tableName)) return tableName;
            var parts = tableName.Split('.');
            return string.Join(".", parts.Select(p => $"[{p}]"));
        }
    }
}
