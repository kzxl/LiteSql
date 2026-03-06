using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LiteSql.Mapping
{
    /// <summary>
    /// Represents the mapping metadata for a single column.
    /// </summary>
    public class ColumnMapping
    {
        public PropertyInfo Property { get; set; }
        public string ColumnName { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsDbGenerated { get; set; }
        public bool CanBeNull { get; set; }
        public AutoSync AutoSync { get; set; }
    }

    /// <summary>
    /// Represents the mapping metadata for an entity type,
    /// including table name and all column mappings.
    /// </summary>
    public class EntityMapping
    {
        public Type EntityType { get; set; }
        public string TableName { get; set; }
        public IReadOnlyList<ColumnMapping> Columns { get; set; }
        public IReadOnlyList<ColumnMapping> PrimaryKeys { get; set; }
        public IReadOnlyList<ColumnMapping> InsertableColumns { get; set; }
        public IReadOnlyList<ColumnMapping> UpdatableColumns { get; set; }
    }

    /// <summary>
    /// Thread-safe cache for entity mapping metadata.
    /// Reads [Table] and [Column] attributes via reflection and caches results.
    /// </summary>
    public static class MappingCache
    {
        private static readonly ConcurrentDictionary<Type, EntityMapping> Cache
            = new ConcurrentDictionary<Type, EntityMapping>();

        /// <summary>
        /// Gets the mapping metadata for the specified entity type.
        /// Results are cached for subsequent calls.
        /// </summary>
        public static EntityMapping GetMapping<T>() where T : class
        {
            return GetMapping(typeof(T));
        }

        /// <summary>
        /// Gets the mapping metadata for the specified entity type.
        /// Results are cached for subsequent calls.
        /// </summary>
        public static EntityMapping GetMapping(Type entityType)
        {
            return Cache.GetOrAdd(entityType, BuildMapping);
        }

        private static EntityMapping BuildMapping(Type type)
        {
            // Resolve table name from [Table] attribute or class name
            var tableAttr = type.GetCustomAttribute<TableAttribute>();
            var tableName = tableAttr?.Name ?? type.Name;

            // Build column mappings from properties with [Column] attribute
            var allColumns = new List<ColumnMapping>();

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var colAttr = prop.GetCustomAttribute<ColumnAttribute>();

                // If entity has any [Column] attributes, only map attributed properties
                // If entity has NO [Column] attributes, map all public properties (convention-based)
                if (colAttr != null)
                {
                    allColumns.Add(new ColumnMapping
                    {
                        Property = prop,
                        ColumnName = colAttr.Name ?? prop.Name,
                        IsPrimaryKey = colAttr.IsPrimaryKey,
                        IsDbGenerated = colAttr.IsDbGenerated,
                        CanBeNull = colAttr.CanBeNull,
                        AutoSync = colAttr.AutoSync
                    });
                }
            }

            // Convention-based: if no [Column] attributes found, map all properties
            if (allColumns.Count == 0)
            {
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    // Skip navigation/association properties
                    if (prop.GetCustomAttribute<AssociationAttribute>() != null)
                        continue;

                    // Skip collection properties (likely navigation)
                    if (prop.PropertyType != typeof(string) &&
                        typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType))
                        continue;

                    allColumns.Add(new ColumnMapping
                    {
                        Property = prop,
                        ColumnName = prop.Name,
                        IsPrimaryKey = false,
                        IsDbGenerated = false,
                        CanBeNull = !prop.PropertyType.IsValueType ||
                                    Nullable.GetUnderlyingType(prop.PropertyType) != null
                    });
                }
            }

            var primaryKeys = allColumns.Where(c => c.IsPrimaryKey).ToList();
            var insertable = allColumns.Where(c => !c.IsDbGenerated).ToList();
            var updatable = allColumns.Where(c => !c.IsPrimaryKey && !c.IsDbGenerated).ToList();

            return new EntityMapping
            {
                EntityType = type,
                TableName = tableName,
                Columns = allColumns,
                PrimaryKeys = primaryKeys,
                InsertableColumns = insertable,
                UpdatableColumns = updatable
            };
        }

        /// <summary>
        /// Clears the mapping cache. Useful for testing.
        /// </summary>
        public static void Clear()
        {
            Cache.Clear();
        }
    }
}
