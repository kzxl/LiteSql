using LiteSql.Mapping;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace LiteSql.ChangeTracking
{
    /// <summary>
    /// Tracks pending Insert, Delete, and Update operations for SubmitChanges().
    /// Update tracking uses snapshot-based comparison: original values are captured
    /// when entities are first loaded, and compared at SubmitChanges() time.
    /// Uses compiled expression delegates for high performance property access (OPT-1).
    /// </summary>
    public class ChangeTracker
    {
        private readonly List<TrackedEntity> _trackedEntities = new List<TrackedEntity>();
        private readonly Dictionary<object, Dictionary<string, object>> _originalValues
            = new Dictionary<object, Dictionary<string, object>>(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Compiled getter cache: avoids slow PropertyInfo.GetValue() reflection.
        /// Each PropertyInfo is compiled once into a Func&lt;object, object&gt; delegate.
        /// ~3-5x faster than reflection for repeated property access.
        /// </summary>
        private static readonly ConcurrentDictionary<PropertyInfo, Func<object, object>> _getterCache
            = new ConcurrentDictionary<PropertyInfo, Func<object, object>>();

        private static Func<object, object> GetCompiledGetter(PropertyInfo prop)
        {
            return _getterCache.GetOrAdd(prop, p =>
            {
                var param = Expression.Parameter(typeof(object));
                var cast = Expression.Convert(param, p.DeclaringType);
                var access = Expression.Property(cast, p);
                var box = Expression.Convert(access, typeof(object));
                return Expression.Lambda<Func<object, object>>(box, param).Compile();
            });
        }

        /// <summary>
        /// Marks an entity for insertion.
        /// </summary>
        public void TrackInsert<T>(T entity) where T : class
        {
            _trackedEntities.Add(new TrackedEntity(entity, typeof(T), EntityState.Insert));
        }

        /// <summary>
        /// Marks an entity for deletion.
        /// </summary>
        public void TrackDelete<T>(T entity) where T : class
        {
            _trackedEntities.Add(new TrackedEntity(entity, typeof(T), EntityState.Delete));
        }

        /// <summary>
        /// Captures the original property values of a loaded entity for later change detection.
        /// Call this for each entity loaded from the database.
        /// </summary>
        public void TrackLoaded(object entity, EntityMapping mapping)
        {
            if (entity == null || _originalValues.ContainsKey(entity))
                return;

            var snapshot = new Dictionary<string, object>();
            foreach (var col in mapping.Columns)
            {
                snapshot[col.Property.Name] = GetCompiledGetter(col.Property)(entity);
            }
            _originalValues[entity] = snapshot;
        }

        /// <summary>
        /// Tracks an entity using another entity's values as the original snapshot.
        /// Used by Attach(entity, original) to set the baseline for change detection.
        /// </summary>
        public void TrackLoadedWithOriginal(object entity, object original, EntityMapping mapping)
        {
            if (entity == null) return;

            var snapshot = new Dictionary<string, object>();
            foreach (var col in mapping.Columns)
            {
                snapshot[col.Property.Name] = GetCompiledGetter(col.Property)(original);
            }
            _originalValues[entity] = snapshot;
        }

        /// <summary>
        /// Explicitly marks an entity as modified (for Attach with asModified=true).
        /// </summary>
        public void TrackUpdate(object entity, Type entityType)
        {
            if (entity == null) return;
            _trackedEntities.Add(new TrackedEntity(entity, entityType, EntityState.Update));
        }

        /// <summary>
        /// Detects modified entities by comparing current values against original snapshots.
        /// Returns tracked entities with state=Update, including ChangedProperties for dirty update.
        /// </summary>
        public List<TrackedEntity> DetectChanges(EntityMapping mapping)
        {
            var updates = new List<TrackedEntity>();

            foreach (var kvp in _originalValues)
            {
                var entity = kvp.Key;
                var originalSnapshot = kvp.Value;

                if (entity.GetType() != mapping.EntityType)
                    continue;

                var changedProps = new List<string>();

                foreach (var col in mapping.UpdatableColumns)
                {
                    var currentValue = GetCompiledGetter(col.Property)(entity);
                    var originalValue = originalSnapshot.ContainsKey(col.Property.Name)
                        ? originalSnapshot[col.Property.Name]
                        : null;

                    if (!Equals(currentValue, originalValue))
                    {
                        changedProps.Add(col.Property.Name);
                    }
                }

                if (changedProps.Count > 0)
                {
                    updates.Add(new TrackedEntity(entity, mapping.EntityType, EntityState.Update)
                    {
                        ChangedProperties = changedProps
                    });
                }
            }

            return updates;
        }

        /// <summary>
        /// Returns all pending changes (inserts, deletes, and detected updates).
        /// </summary>
        public IReadOnlyList<TrackedEntity> GetPendingChanges()
        {
            return _trackedEntities.Where(e =>
                e.State == EntityState.Insert ||
                e.State == EntityState.Delete ||
                e.State == EntityState.Update
            ).ToList().AsReadOnly();
        }

        /// <summary>
        /// Clears all tracked changes and snapshots after a successful SubmitChanges.
        /// </summary>
        public void AcceptChanges()
        {
            _trackedEntities.Clear();
            _originalValues.Clear();
        }

        /// <summary>
        /// Returns true if there are any pending changes.
        /// </summary>
        public bool HasChanges => _trackedEntities.Any(e =>
            e.State == EntityState.Insert ||
            e.State == EntityState.Delete ||
            e.State == EntityState.Update);

        /// <summary>
        /// Adds detected updates to the pending changes list.
        /// Called by LiteContext before processing SubmitChanges.
        /// </summary>
        public void AddUpdates(IEnumerable<TrackedEntity> updates)
        {
            _trackedEntities.AddRange(updates);
        }
    }

    /// <summary>
    /// Compares objects by reference identity (not Equals). Required for tracking
    /// different entity instances even if they have equal property values.
    /// </summary>
    internal class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

        bool IEqualityComparer<object>.Equals(object x, object y) =>
            ReferenceEquals(x, y);

        int IEqualityComparer<object>.GetHashCode(object obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

