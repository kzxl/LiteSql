using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;

namespace LiteSql.CodeGen
{
    /// <summary>
    /// Reads database schema directly from SQL Server using INFORMATION_SCHEMA views.
    /// Returns a DbmlModel so the existing CodeGenerator can be reused.
    /// </summary>
    public class DatabaseSchemaReader
    {
        /// <summary>
        /// Connects to SQL Server and reads all tables, columns, and FK relationships.
        /// </summary>
        public static DbmlModel ReadSchema(IDbConnection connection, string contextClassName = null)
        {
            if (connection.State != ConnectionState.Open)
                connection.Open();

            var dbName = connection.Database;
            var model = new DbmlModel
            {
                DatabaseName = dbName,
                ContextClassName = contextClassName ?? $"{SanitizeName(dbName)}DataContext"
            };

            // 1. Read all tables
            var tables = ReadTables(connection);

            // 2. Read columns for each table
            foreach (var table in tables)
            {
                var parts = table.DbTableName.Split('.');
                var schemaName = parts.Length > 1 ? parts[0] : "dbo";
                var rawTableName = parts.Length > 1 ? parts[1] : parts[0];
                var columns = ReadColumns(connection, schemaName, rawTableName);
                table.Columns.AddRange(columns);
                model.Tables.Add(table);
            }

            // 3. Read FK relationships
            var fks = ReadForeignKeys(connection);
            foreach (var fk in fks)
            {
                // Add association to parent table (the one with PK)
                var parentTable = model.Tables.FirstOrDefault(t =>
                    t.DbTableName == $"{fk.PkSchema}.{fk.PkTable}" ||
                    t.TypeName == fk.PkTable);
                var childTable = model.Tables.FirstOrDefault(t =>
                    t.DbTableName == $"{fk.FkSchema}.{fk.FkTable}" ||
                    t.TypeName == fk.FkTable);

                if (parentTable != null && childTable != null)
                {
                    // FK side (child → parent): EntityRef
                    childTable.Associations.Add(new DbmlAssociation
                    {
                        Name = fk.ConstraintName,
                        MemberName = parentTable.TypeName,  // e.g. "tbINV_Warehouse"
                        ThisKey = fk.FkColumn,
                        OtherKey = fk.PkColumn,
                        OtherType = parentTable.TypeName,
                        IsForeignKey = true
                    });

                    // PK side (parent → children): EntitySet
                    parentTable.Associations.Add(new DbmlAssociation
                    {
                        Name = fk.ConstraintName,
                        MemberName = childTable.TypeName + "s",  // e.g. "tbINV_StockIns"
                        ThisKey = fk.PkColumn,
                        OtherKey = fk.FkColumn,
                        OtherType = childTable.TypeName,
                        IsForeignKey = false
                    });
                }
            }

            return model;
        }

        private static List<DbmlTable> ReadTables(IDbConnection connection)
        {
            var tables = new List<DbmlTable>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT TABLE_SCHEMA, TABLE_NAME 
                    FROM INFORMATION_SCHEMA.TABLES 
                    WHERE TABLE_TYPE = 'BASE TABLE'
                    ORDER BY TABLE_SCHEMA, TABLE_NAME";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var schema = reader.GetString(0);
                        var tableName = reader.GetString(1);
                        tables.Add(new DbmlTable
                        {
                            DbTableName = $"{schema}.{tableName}",
                            TypeName = tableName,
                            MemberName = tableName + "s"
                        });
                    }
                }
            }
            return tables;
        }

        private static List<DbmlColumn> ReadColumns(IDbConnection connection, string schema, string tableName)
        {
            var columns = new List<DbmlColumn>();
            var pkColumns = ReadPrimaryKeyColumns(connection, schema, tableName);

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT 
                        c.COLUMN_NAME,
                        c.DATA_TYPE,
                        c.IS_NULLABLE,
                        c.CHARACTER_MAXIMUM_LENGTH,
                        c.NUMERIC_PRECISION,
                        c.NUMERIC_SCALE,
                        COLUMNPROPERTY(OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME), c.COLUMN_NAME, 'IsIdentity') as IS_IDENTITY
                    FROM INFORMATION_SCHEMA.COLUMNS c
                    WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
                    ORDER BY c.ORDINAL_POSITION";

                var schemaParam = cmd.CreateParameter();
                schemaParam.ParameterName = "@schema";
                schemaParam.Value = schema;
                cmd.Parameters.Add(schemaParam);

                var tableParam = cmd.CreateParameter();
                tableParam.ParameterName = "@table";
                tableParam.Value = tableName;
                cmd.Parameters.Add(tableParam);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var columnName = reader.GetString(0);
                        var dataType = reader.GetString(1);
                        var isNullable = reader.GetString(2) == "YES";
                        var maxLength = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
                        var isIdentity = !reader.IsDBNull(6) && reader.GetInt32(6) == 1;
                        var isPk = pkColumns.Contains(columnName);

                        var dbType = BuildDbType(dataType, maxLength, isNullable, isIdentity, isPk);

                        columns.Add(new DbmlColumn
                        {
                            Name = columnName,
                            DbType = dbType,
                            ClrType = MapSqlTypeToCLR(dataType),
                            IsPrimaryKey = isPk,
                            IsDbGenerated = isIdentity,
                            CanBeNull = isNullable
                        });
                    }
                }
            }
            return columns;
        }

        private static HashSet<string> ReadPrimaryKeyColumns(IDbConnection connection, string schema, string tableName)
        {
            var pks = new HashSet<string>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT cu.COLUMN_NAME
                    FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                    JOIN INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE cu
                        ON tc.CONSTRAINT_NAME = cu.CONSTRAINT_NAME
                    WHERE tc.TABLE_SCHEMA = @schema
                      AND tc.TABLE_NAME = @table
                      AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'";

                var p1 = cmd.CreateParameter();
                p1.ParameterName = "@schema";
                p1.Value = schema;
                cmd.Parameters.Add(p1);

                var p2 = cmd.CreateParameter();
                p2.ParameterName = "@table";
                p2.Value = tableName;
                cmd.Parameters.Add(p2);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        pks.Add(reader.GetString(0));
                }
            }
            return pks;
        }

        private static List<ForeignKeyInfo> ReadForeignKeys(IDbConnection connection)
        {
            var fks = new List<ForeignKeyInfo>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT
                        fk.name AS ConstraintName,
                        SCHEMA_NAME(tp.schema_id) AS PkSchema,
                        tp.name AS PkTable,
                        cp.name AS PkColumn,
                        SCHEMA_NAME(tr.schema_id) AS FkSchema,
                        tr.name AS FkTable,
                        cr.name AS FkColumn
                    FROM sys.foreign_keys fk
                    JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
                    JOIN sys.tables tp ON fkc.referenced_object_id = tp.object_id
                    JOIN sys.columns cp ON fkc.referenced_object_id = cp.object_id AND fkc.referenced_column_id = cp.column_id
                    JOIN sys.tables tr ON fkc.parent_object_id = tr.object_id
                    JOIN sys.columns cr ON fkc.parent_object_id = cr.object_id AND fkc.parent_column_id = cr.column_id
                    ORDER BY fk.name";

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        fks.Add(new ForeignKeyInfo
                        {
                            ConstraintName = reader.GetString(0),
                            PkSchema = reader.GetString(1),
                            PkTable = reader.GetString(2),
                            PkColumn = reader.GetString(3),
                            FkSchema = reader.GetString(4),
                            FkTable = reader.GetString(5),
                            FkColumn = reader.GetString(6)
                        });
                    }
                }
            }
            return fks;
        }

        #region Type Mapping

        private static string MapSqlTypeToCLR(string sqlType)
        {
            switch (sqlType.ToLower())
            {
                case "bigint": return "System.Int64";
                case "int": return "System.Int32";
                case "smallint": return "System.Int16";
                case "tinyint": return "System.Byte";
                case "bit": return "System.Boolean";
                case "decimal":
                case "numeric":
                case "money":
                case "smallmoney": return "System.Decimal";
                case "float": return "System.Double";
                case "real": return "System.Single";
                case "datetime":
                case "datetime2":
                case "smalldatetime":
                case "date": return "System.DateTime";
                case "datetimeoffset": return "System.DateTimeOffset";
                case "time": return "System.TimeSpan";
                case "uniqueidentifier": return "System.Guid";
                case "varbinary":
                case "binary":
                case "image":
                case "timestamp": return "System.Data.Linq.Binary";
                default: return "System.String";
            }
        }

        private static string BuildDbType(string dataType, int? maxLength, bool isNullable, bool isIdentity, bool isPk)
        {
            var parts = new List<string>();

            // Type name
            var typeName = dataType.ToUpper();
            switch (dataType.ToLower())
            {
                case "nvarchar":
                case "varchar":
                case "nchar":
                case "char":
                    typeName = maxLength == -1
                        ? $"{typeName}(MAX)"
                        : $"{typeName}({maxLength})";
                    break;
                case "varbinary":
                case "binary":
                    typeName = maxLength == -1
                        ? $"{typeName}(MAX)"
                        : $"{typeName}({maxLength})";
                    break;
            }

            parts.Add(typeName);

            if (!isNullable) parts.Add("NOT NULL");
            if (isIdentity) parts.Add("IDENTITY");

            return string.Join(" ", parts);
        }

        private static string SanitizeName(string name)
        {
            return new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        }

        #endregion

        private class ForeignKeyInfo
        {
            public string ConstraintName { get; set; }
            public string PkSchema { get; set; }
            public string PkTable { get; set; }
            public string PkColumn { get; set; }
            public string FkSchema { get; set; }
            public string FkTable { get; set; }
            public string FkColumn { get; set; }
        }
    }

    // Extension to parse schema.table
    internal static class DbmlTableExtensions
    {
        public static string SchemaName(this DbmlTable table)
        {
            var parts = table.DbTableName.Split('.');
            return parts.Length > 1 ? parts[0] : "dbo";
        }
    }
}
