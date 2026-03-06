using LiteSql.ChangeTracking;
using LiteSql.Mapping;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace LiteSql
{
    /// <summary>
    /// Represents a database table for entity type T.
    /// Compatible with System.Data.Linq.Table&lt;T&gt;.
    /// Provides InsertOnSubmit/DeleteOnSubmit for change tracking,
    /// and IEnumerable&lt;T&gt; for LINQ queries (loads data from DB).
    /// </summary>
    public class Table<T> : IEnumerable<T> where T : class
    {
        private readonly LiteContext _context;
        private readonly ChangeTracker _changeTracker;

        internal Table(LiteContext context, ChangeTracker changeTracker)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _changeTracker = changeTracker ?? throw new ArgumentNullException(nameof(changeTracker));
        }

        /// <summary>
        /// Marks an entity for insertion on next SubmitChanges().
        /// Compatible with System.Data.Linq.Table&lt;T&gt;.InsertOnSubmit().
        /// </summary>
        public void InsertOnSubmit(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            _changeTracker.TrackInsert(entity);
        }

        /// <summary>
        /// Marks multiple entities for insertion on next SubmitChanges().
        /// Compatible with System.Data.Linq.Table&lt;T&gt;.InsertAllOnSubmit().
        /// </summary>
        public void InsertAllOnSubmit(IEnumerable<T> entities)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));
            foreach (var entity in entities)
            {
                InsertOnSubmit(entity);
            }
        }

        /// <summary>
        /// Marks an entity for deletion on next SubmitChanges().
        /// Compatible with System.Data.Linq.Table&lt;T&gt;.DeleteOnSubmit().
        /// </summary>
        public void DeleteOnSubmit(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            _changeTracker.TrackDelete(entity);
        }

        /// <summary>
        /// Marks multiple entities for deletion on next SubmitChanges().
        /// Compatible with System.Data.Linq.Table&lt;T&gt;.DeleteAllOnSubmit().
        /// </summary>
        public void DeleteAllOnSubmit(IEnumerable<T> entities)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));
            foreach (var entity in entities)
            {
                DeleteOnSubmit(entity);
            }
        }

        /// <summary>
        /// Returns an IQueryable&lt;T&gt; for composing LINQ queries.
        /// Queries will be loaded from DB via Dapper when enumerated.
        /// </summary>
        public IQueryable<T> AsQueryable()
        {
            // Phase 1: Load all rows then return as IQueryable for in-memory filtering.
            // Phase 2: Replace with custom IQueryProvider for SQL translation.
            return GetAll().AsQueryable();
        }

        /// <summary>
        /// Loads all rows from the database table via Dapper.
        /// </summary>
        private List<T> GetAll()
        {
            return _context.ExecuteQuery<T>(
                Sql.SqlGenerator.GenerateSelectAll(MappingCache.GetMapping<T>())
            ).ToList();
        }

        /// <summary>
        /// Enumerates all rows from the database table.
        /// Phase 1: Loads all data into memory via Dapper.
        /// </summary>
        public IEnumerator<T> GetEnumerator()
        {
            return GetAll().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
