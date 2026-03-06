using Dapper;
using LiteSql.ChangeTracking;
using LiteSql.Mapping;
using LiteSql.Sql;
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

        #region Insert / Delete

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

        #endregion

        #region Querying

        /// <summary>
        /// Executes a SQL WHERE query using a LINQ expression predicate.
        /// Translates the expression into SQL for server-side filtering.
        /// Example: table.Where(p => p.IsActive && p.Price > 100)
        /// </summary>
        public IEnumerable<T> Where(Expression<Func<T, bool>> predicate)
        {
            var mapping = MappingCache.GetMapping<T>();
            var builder = new WhereBuilder(mapping);
            var (whereSql, parameters) = builder.Build(predicate);

            var selectSql = SqlGenerator.GenerateSelectAll(mapping);
            var fullSql = $"{selectSql} WHERE {whereSql}";

            _context.EnsureConnectionOpen();

            var dp = new DynamicParameters();
            foreach (var kv in parameters)
                dp.Add(kv.Key, kv.Value);

            var results = _context.Connection.Query<T>(fullSql, dp,
                transaction: _context.Transaction,
                commandTimeout: _context.CommandTimeout).ToList();

            // Track loaded entities for update detection
            if (_context.ObjectTrackingEnabled)
            {
                foreach (var entity in results)
                    _changeTracker.TrackLoaded(entity, mapping);
            }

            return results;
        }

        /// <summary>
        /// Returns an IQueryable&lt;T&gt; for composing LINQ queries.
        /// Queries will be loaded from DB via Dapper when enumerated.
        /// </summary>
        public IQueryable<T> AsQueryable()
        {
            return GetAll().AsQueryable();
        }

        /// <summary>
        /// Loads all rows from the database table via Dapper.
        /// </summary>
        private List<T> GetAll()
        {
            var results = _context.ExecuteQuery<T>(
                SqlGenerator.GenerateSelectAll(MappingCache.GetMapping<T>())
            ).ToList();

            // Track loaded entities for update detection
            if (_context.ObjectTrackingEnabled)
            {
                var mapping = MappingCache.GetMapping<T>();
                foreach (var entity in results)
                    _changeTracker.TrackLoaded(entity, mapping);
            }

            return results;
        }

        /// <summary>
        /// Enumerates all rows from the database table.
        /// Loads all data into memory via Dapper and tracks for updates.
        /// </summary>
        public IEnumerator<T> GetEnumerator()
        {
            return GetAll().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        #endregion
    }
}
