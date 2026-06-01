using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using Dapper;
using LiteSql.Dialects;
using LiteSql.Mapping;
using LiteSql.Sql;

namespace LiteSql
{
    /// <summary>
    /// Provides bulk operations for high-performance data manipulation.
    /// Batches statements to stay within provider parameter limits.
    /// </summary>
    public static class BulkOperations
    {
        // SQL Server caps parameters at 2100; stay well under to be safe across providers.
        private const int MaxParametersPerCommand = 2000;

        /// <summary>
        /// Inserts multiple entities using multi-row INSERT, automatically chunked to
        /// respect the provider's parameter limit.
        /// </summary>
        public static int BulkInsert<T>(IDbConnection connection, IEnumerable<T> entities,
            IDbTransaction transaction = null, ISqlDialect dialect = null) where T : class
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (entities == null) throw new ArgumentNullException(nameof(entities));

            var d = dialect ?? SqlGenerator.DefaultDialect;
            var entityList = entities.ToList();
            if (entityList.Count == 0) return 0;

            var mapping = MappingCache.GetMapping<T>();
            var insertColumns = mapping.Columns.Where(c => !c.IsDbGenerated).ToList();
            if (insertColumns.Count == 0)
                throw new InvalidOperationException($"Entity {typeof(T).Name} has no insertable columns.");

            var table = SqlGenerator.QuoteTableName(mapping.TableName, d);
            var columnNames = string.Join(", ", insertColumns.Select(c => d.QuoteIdentifier(c.ColumnName)));

            // Chunk so (rows * columns) stays under the parameter limit.
            var rowsPerChunk = Math.Max(1, MaxParametersPerCommand / insertColumns.Count);
            int affected = 0;

            foreach (var chunk in Chunk(entityList, rowsPerChunk))
            {
                var parameters = new DynamicParameters();
                var valuesClauses = new List<string>();
                for (int i = 0; i < chunk.Count; i++)
                {
                    var paramNames = new List<string>();
                    foreach (var col in insertColumns)
                    {
                        var paramName = $"@p{i}_{col.Property.Name}";
                        parameters.Add(paramName, col.Property.GetValue(chunk[i]));
                        paramNames.Add(paramName);
                    }
                    valuesClauses.Add($"({string.Join(", ", paramNames)})");
                }

                var sql = $"INSERT INTO {table} ({columnNames}) VALUES {string.Join(", ", valuesClauses)}";
                affected += connection.Execute(sql, parameters, transaction);
            }

            return affected;
        }

        /// <summary>
        /// Updates multiple entities by primary key(s) in a single parameterized command list.
        /// Supports composite primary keys.
        /// </summary>
        public static int BulkUpdate<T>(IDbConnection connection, IEnumerable<T> entities,
            IDbTransaction transaction = null, ISqlDialect dialect = null) where T : class
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (entities == null) throw new ArgumentNullException(nameof(entities));

            var d = dialect ?? SqlGenerator.DefaultDialect;
            var entityList = entities.ToList();
            if (entityList.Count == 0) return 0;

            var mapping = MappingCache.GetMapping<T>();
            var primaryKeys = mapping.Columns.Where(c => c.IsPrimaryKey).ToList();
            if (primaryKeys.Count == 0)
                throw new InvalidOperationException($"Entity {typeof(T).Name} has no primary key defined.");

            var setColumns = mapping.Columns
                .Where(c => !c.IsPrimaryKey && !c.IsDbGenerated)
                .Select(c => $"{d.QuoteIdentifier(c.ColumnName)} = @{c.Property.Name}")
                .ToList();

            if (setColumns.Count == 0)
                throw new InvalidOperationException($"Entity {typeof(T).Name} has no updatable columns.");

            var whereClause = string.Join(" AND ",
                primaryKeys.Select(pk => $"{d.QuoteIdentifier(pk.ColumnName)} = @{pk.Property.Name}"));

            var table = SqlGenerator.QuoteTableName(mapping.TableName, d);
            var sql = $"UPDATE {table} SET {string.Join(", ", setColumns)} WHERE {whereClause}";

            return connection.Execute(sql, entityList, transaction);
        }

        /// <summary>
        /// Deletes multiple entities by their primary keys.
        /// Single-column PKs use a chunked IN clause; composite PKs use per-row predicates.
        /// </summary>
        public static int BulkDelete<T>(IDbConnection connection, IEnumerable<T> entities,
            IDbTransaction transaction = null, ISqlDialect dialect = null) where T : class
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (entities == null) throw new ArgumentNullException(nameof(entities));

            var d = dialect ?? SqlGenerator.DefaultDialect;
            var entityList = entities.ToList();
            if (entityList.Count == 0) return 0;

            var mapping = MappingCache.GetMapping<T>();
            var primaryKeys = mapping.Columns.Where(c => c.IsPrimaryKey).ToList();
            if (primaryKeys.Count == 0)
                throw new InvalidOperationException($"Entity {typeof(T).Name} has no primary key defined.");

            var table = SqlGenerator.QuoteTableName(mapping.TableName, d);

            if (primaryKeys.Count == 1)
            {
                // Single PK: chunked DELETE ... WHERE pk IN (...)
                var pk = primaryKeys[0];
                var pkCol = d.QuoteIdentifier(pk.ColumnName);
                int affected = 0;

                foreach (var chunk in Chunk(entityList, MaxParametersPerCommand))
                {
                    var parameters = new DynamicParameters();
                    var paramNames = new List<string>();
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        var name = $"@id{i}";
                        paramNames.Add(name);
                        parameters.Add(name, pk.Property.GetValue(chunk[i]));
                    }
                    var sql = $"DELETE FROM {table} WHERE {pkCol} IN ({string.Join(", ", paramNames)})";
                    affected += connection.Execute(sql, parameters, transaction);
                }

                return affected;
            }
            else
            {
                // Composite PK: per-row predicates batched into chunks.
                int affected = 0;
                var paramsPerRow = primaryKeys.Count;
                var rowsPerChunk = Math.Max(1, MaxParametersPerCommand / paramsPerRow);

                foreach (var chunk in Chunk(entityList, rowsPerChunk))
                {
                    var parameters = new DynamicParameters();
                    var rowClauses = new List<string>();
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        var conds = new List<string>();
                        foreach (var pk in primaryKeys)
                        {
                            var name = $"@pk{i}_{pk.Property.Name}";
                            conds.Add($"{d.QuoteIdentifier(pk.ColumnName)} = {name}");
                            parameters.Add(name, pk.Property.GetValue(chunk[i]));
                        }
                        rowClauses.Add($"({string.Join(" AND ", conds)})");
                    }
                    var sql = $"DELETE FROM {table} WHERE {string.Join(" OR ", rowClauses)}";
                    affected += connection.Execute(sql, parameters, transaction);
                }

                return affected;
            }
        }

        private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
        {
            for (int i = 0; i < source.Count; i += size)
                yield return source.Skip(i).Take(size).ToList();
        }
    }
}
