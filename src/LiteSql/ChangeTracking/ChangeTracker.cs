using LiteSql.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LiteSql.ChangeTracking
{
    /// <summary>
    /// Tracks pending Insert, Delete, and Update operations for SubmitChanges().
    /// Update tracking uses snapshot-based comparison: original values are captured
    /// when entities are first loaded, and compared at SubmitChanges() time.
    /// </summary>
    public class ChangeTracker
    {
        private readonly List<TrackedEntity> _trackedEntities = new List<TrackedEntity>();
        private readonly Dictionary<object, Dictionary<string, object>> _originalValues
            = new Dictionary<object, Dictionary<string, object>>(ReferenceEqualityComparer.Instance);

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
                snapshot[col.Property.Name] = col.Property.GetValue(entity);
            }
            _originalValues[entity] = snapshot;
        }

        /// <summary>
        /// Detects modified entities by comparing current values against original snapshots.
        /// Returns tracked entities with state=Update for any entity whose property values changed.
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

                foreach (var col in mapping.UpdatableColumns)
                {
                    var currentValue = col.Property.GetValue(entity);
                    var originalValue = originalSnapshot.ContainsKey(col.Property.Name)
                        ? originalSnapshot[col.Property.Name]
                        : null;

                    if (!Equals(currentValue, originalValue))
                    {
                        updates.Add(new TrackedEntity(entity, mapping.EntityType, EntityState.Update));
                        break; // Only need to detect one change per entity
                    }
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
