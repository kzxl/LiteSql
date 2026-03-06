using System;

namespace LiteSql.ChangeTracking
{
    /// <summary>
    /// Wraps an entity with its tracking state and type information.
    /// </summary>
    public class TrackedEntity
    {
        public object Entity { get; }
        public Type EntityType { get; }
        public EntityState State { get; internal set; }

        public TrackedEntity(object entity, Type entityType, EntityState state)
        {
            Entity = entity ?? throw new ArgumentNullException(nameof(entity));
            EntityType = entityType ?? throw new ArgumentNullException(nameof(entityType));
            State = state;
        }
    }
}
