using LiteSql.ChangeTracking;
using LiteSql.Mapping;
using System;
using System.Collections.Concurrent;
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
        // OPT-2: SQL template caches per entity type
        private static readonly ConcurrentDictionary<Type, string> _insertSqlCache = new ConcurrentDictionary<Type, string>();
        private static readonly ConcurrentDictionary<Type, string> _deleteSqlCache = new ConcurrentDictionary<Type, string>();
        private static readonly ConcurrentDictionary<Type, string> _selectAllCache = new ConcurrentDictionary<Type, string>();
        /// <summary>
        /// Generates a SELECT * statement for the entity type.
        /// </summary>
        public static string GenerateSelectAll(EntityMapping mapping)
        {
            return _selectAllCache.GetOrAdd(mapping.EntityType,
                _ => $"SELECT * FROM {QuoteTableName(mapping.TableName)}");
        }

        /// <summary>
        /// Generates a parameterized INSERT statement and its parameter dictionary.
        /// Skips IsDbGenerated columns.
        /// Returns the generated identity value if any PK is IsDbGenerated.
        /// </summary>
        public static (string Sql, IDictionary<string, object> Parameters) GenerateInsert(
            EntityMapping mapping, object entity)
        {
            var sql = _insertSqlCache.GetOrAdd(mapping.EntityType, _ =>
            {
                var columns = mapping.InsertableColumns;
                var columnNames = columns.Select(c => $"[{c.ColumnName}]");
                var paramNames = columns.Select(c => $"@{c.ColumnName}");
                return $"INSERT INTO {QuoteTableName(mapping.TableName)} ({string.Join(", ", columnNames)}) " +
                       $"VALUES ({string.Join(", ", paramNames)})";
            });

            var parameters = BuildParameters(mapping.InsertableColumns, entity);
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

            var sqlTemplate = _deleteSqlCache.GetOrAdd(mapping.EntityType, _ =>
            {
                var pkClauses = mapping.PrimaryKeys.Select(pk => $"[{pk.ColumnName}] = @pk_{pk.ColumnName}");
                return $"DELETE FROM {QuoteTableName(mapping.TableName)} WHERE {string.Join(" AND ", pkClauses)}";
            });

            var parameters = new Dictionary<string, object>();
            foreach (var pk in mapping.PrimaryKeys)
                parameters[$"@pk_{pk.ColumnName}"] = pk.Property.GetValue(entity);

            return (sqlTemplate, parameters);
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
        /// Generates a partial UPDATE statement — only SET columns that have changed.
        /// Used by dirty update feature when ChangedProperties is available.
        /// </summary>
        public static (string Sql, IDictionary<string, object> Parameters) GeneratePartialUpdate(
            EntityMapping mapping, object entity, IReadOnlyList<string> changedProperties)
        {
            if (mapping.PrimaryKeys.Count == 0)
                throw new InvalidOperationException(
                    $"Cannot generate UPDATE for '{mapping.EntityType.Name}': no primary key defined.");

            // Filter updatable columns to only changed ones
            var changedColumns = mapping.UpdatableColumns
                .Where(c => changedProperties.Contains(c.Property.Name))
                .ToList();

            if (changedColumns.Count == 0)
                return (null, null); // No changes to persist

            var setClauses = changedColumns.Select(c => $"[{c.ColumnName}] = @{c.ColumnName}");
            var whereClause = BuildWhereByPrimaryKeys(mapping, entity, out var whereParams);

            var sql = $"UPDATE {QuoteTableName(mapping.TableName)} " +
                      $"SET {string.Join(", ", setClauses)} " +
                      $"WHERE {whereClause}";

            var allParams = BuildParameters(changedColumns, entity);
            foreach (var kv in whereParams)
                allParams[kv.Key] = kv.Value;

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

        /// <summary>
        /// Generates an UPSERT (INSERT OR UPDATE) SQL statement.
        /// SQLite: INSERT OR REPLACE INTO ...
        /// SQL Server: MERGE ... WHEN MATCHED THEN UPDATE WHEN NOT MATCHED THEN INSERT
        /// </summary>
        public static (string sql, IDictionary<string, object> parameters)? GenerateUpsert(
            EntityMapping mapping, object entity, bool isSqlite = true)
        {
            if (mapping.PrimaryKeys.Count == 0) return null;

            var allCols = mapping.Columns.Where(c => !c.IsDbGenerated).ToList();
            var parameters = BuildParameters(allCols, entity);

            // Also add PK params for MERGE ON clause
            foreach (var pk in mapping.PrimaryKeys)
            {
                var pkParam = $"@pk_{pk.ColumnName}";
                if (!parameters.ContainsKey(pkParam))
                    parameters[pkParam] = pk.Property.GetValue(entity);
            }

            if (isSqlite)
            {
                // SQLite: INSERT OR REPLACE INTO table (cols) VALUES (vals)
                var columnNames = allCols.Select(c => $"[{c.ColumnName}]");
                var paramNames = allCols.Select(c => $"@{c.ColumnName}");
                var sql = $"INSERT OR REPLACE INTO {QuoteTableName(mapping.TableName)} " +
                          $"({string.Join(", ", columnNames)}) VALUES ({string.Join(", ", paramNames)})";
                return (sql, parameters);
            }
            else
            {
                // SQL Server: MERGE
                var onClause = string.Join(" AND ",
                    mapping.PrimaryKeys.Select(pk => $"T.[{pk.ColumnName}] = S.[{pk.ColumnName}]"));
                var updateCols = allCols.Where(c => !c.IsPrimaryKey).ToList();
                var setClauses = updateCols.Select(c => $"T.[{c.ColumnName}] = @{c.ColumnName}");
                var insertCols = allCols.Select(c => $"[{c.ColumnName}]");
                var insertVals = allCols.Select(c => $"@{c.ColumnName}");

                var sql = $"MERGE {QuoteTableName(mapping.TableName)} AS T " +
                          $"USING (SELECT {string.Join(", ", mapping.PrimaryKeys.Select(pk => $"@pk_{pk.ColumnName} AS [{pk.ColumnName}]"))}) AS S " +
                          $"ON {onClause} " +
                          $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", setClauses)} " +
                          $"WHEN NOT MATCHED THEN INSERT ({string.Join(", ", insertCols)}) VALUES ({string.Join(", ", insertVals)});";
                return (sql, parameters);
            }
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
