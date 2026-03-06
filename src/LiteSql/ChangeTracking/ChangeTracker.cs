using System.Collections.Generic;
using System.Linq;

namespace LiteSql.ChangeTracking
{
    /// <summary>
    /// Tracks pending Insert and Delete operations for SubmitChanges().
    /// Phase 1: Insert + Delete only. Phase 2 will add Update tracking via snapshots.
    /// </summary>
    public class ChangeTracker
    {
        private readonly List<TrackedEntity> _trackedEntities = new List<TrackedEntity>();

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
        /// Returns all pending changes (inserts and deletes).
        /// </summary>
        public IReadOnlyList<TrackedEntity> GetPendingChanges()
        {
            return _trackedEntities.Where(e =>
                e.State == EntityState.Insert || e.State == EntityState.Delete
            ).ToList().AsReadOnly();
        }

        /// <summary>
        /// Clears all tracked changes after a successful SubmitChanges.
        /// </summary>
        public void AcceptChanges()
        {
            _trackedEntities.Clear();
        }

        /// <summary>
        /// Returns true if there are any pending changes.
        /// </summary>
        public bool HasChanges => _trackedEntities.Any(e =>
            e.State == EntityState.Insert || e.State == EntityState.Delete);
    }
}
