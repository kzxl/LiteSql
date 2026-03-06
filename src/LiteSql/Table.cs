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

        #region Insert / Delete / Attach

        /// <summary>
        /// Marks an entity for insertion on next SubmitChanges().
        /// </summary>
        public void InsertOnSubmit(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            _changeTracker.TrackInsert(entity);
        }

        /// <summary>
        /// Marks multiple entities for insertion on next SubmitChanges().
        /// </summary>
        public void InsertAllOnSubmit(IEnumerable<T> entities)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));
            foreach (var entity in entities)
                InsertOnSubmit(entity);
        }

        /// <summary>
        /// Marks an entity for deletion on next SubmitChanges().
        /// </summary>
        public void DeleteOnSubmit(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            _changeTracker.TrackDelete(entity);
        }

        /// <summary>
        /// Marks multiple entities for deletion on next SubmitChanges().
        /// </summary>
        public void DeleteAllOnSubmit(IEnumerable<T> entities)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));
            foreach (var entity in entities)
                DeleteOnSubmit(entity);
        }

        /// <summary>
        /// Attaches an entity to the context for update tracking.
        /// Use when you have an entity that was not loaded from this context
        /// (e.g., deserialized or constructed manually) but need to track changes.
        /// Compatible with System.Data.Linq.Table&lt;T&gt;.Attach().
        /// </summary>
        public void Attach(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            var mapping = MappingCache.GetMapping<T>();
            _changeTracker.TrackLoaded(entity, mapping);
        }

        /// <summary>
        /// Attaches an entity with its original state for update tracking.
        /// The original entity is used as the baseline; the current entity values
        /// are compared against original to detect changes.
        /// Compatible with System.Data.Linq.Table&lt;T&gt;.Attach(entity, original).
        /// </summary>
        public void Attach(T entity, T original)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (original == null) throw new ArgumentNullException(nameof(original));
            var mapping = MappingCache.GetMapping<T>();
            _changeTracker.TrackLoadedWithOriginal(entity, original, mapping);
        }

        /// <summary>
        /// Attaches an entity and optionally marks it as modified.
        /// If asModified is true, the entity will be included in the next SubmitChanges UPDATE.
        /// Compatible with System.Data.Linq.Table&lt;T&gt;.Attach(entity, asModified).
        /// </summary>
        public void Attach(T entity, bool asModified)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (asModified)
            {
                _changeTracker.TrackUpdate(entity, typeof(T));
            }
            else
            {
                Attach(entity);
            }
        }

        #endregion

        #region Server-Side Querying

        /// <summary>
        /// Executes a SQL WHERE query using a LINQ expression predicate.
        /// Translates the expression into SQL for server-side filtering.
        /// </summary>
        public IEnumerable<T> Where(Expression<Func<T, bool>> predicate)
        {
            return ExecuteWhere(predicate);
        }

        /// <summary>
        /// Returns the first element matching the predicate, or default(T) if none found.
        /// Executes on the server with TOP 1 for efficiency.
        /// </summary>
        public T FirstOrDefault(Expression<Func<T, bool>> predicate)
        {
            return ExecuteWhere(predicate, top: 1).FirstOrDefault();
        }

        /// <summary>
        /// Returns whether any element matches the predicate.
        /// Executes on the server with COUNT for efficiency.
        /// </summary>
        public bool Any(Expression<Func<T, bool>> predicate)
        {
            return Count(predicate) > 0;
        }

        /// <summary>
        /// Returns the count of elements matching the predicate.
        /// Executes on the server with SELECT COUNT(*).
        /// </summary>
        public int Count(Expression<Func<T, bool>> predicate)
        {
            var mapping = MappingCache.GetMapping<T>();
            var builder = new WhereBuilder(mapping);
            var (whereSql, parameters) = builder.Build(predicate);

            var sql = $"SELECT COUNT(*) FROM [{mapping.TableName}] WHERE {whereSql}";

            _context.EnsureConnectionOpen();

            var dp = ToDynamicParameters(parameters);
            return _context.Connection.ExecuteScalar<int>(sql, dp,
                transaction: _context.Transaction,
                commandTimeout: _context.CommandTimeout);
        }

        /// <summary>
        /// Returns an IQueryable&lt;T&gt; for composing LINQ queries.
        /// </summary>
        public IQueryable<T> AsQueryable()
        {
            return GetAll().AsQueryable();
        }

        #endregion

        #region IEnumerable

        public IEnumerator<T> GetEnumerator()
        {
            return GetAll().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        #endregion

        #region Private Helpers

        private List<T> ExecuteWhere(Expression<Func<T, bool>> predicate, int? top = null)
        {
            var mapping = MappingCache.GetMapping<T>();
            var builder = new WhereBuilder(mapping);
            var (whereSql, parameters) = builder.Build(predicate);

            var selectPart = SqlGenerator.GenerateSelectAll(mapping);
            var fullSql = $"{selectPart} WHERE {whereSql}";

            // Apply row limit (TOP for SQL Server, LIMIT for SQLite)
            if (top.HasValue)
            {
                var isSqlite = _context.Connection.GetType().Name
                    .IndexOf("sqlite", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isSqlite)
                    fullSql += $" LIMIT {top.Value}";
                else
                    fullSql = fullSql.Replace("SELECT ", $"SELECT TOP {top.Value} ");
            }

            _context.EnsureConnectionOpen();

            var dp = ToDynamicParameters(parameters);
            var results = _context.Connection.Query<T>(fullSql, dp,
                transaction: _context.Transaction,
                commandTimeout: _context.CommandTimeout).ToList();

            if (_context.ObjectTrackingEnabled)
            {
                foreach (var entity in results)
                    _changeTracker.TrackLoaded(entity, mapping);
            }

            return results;
        }

        private List<T> GetAll()
        {
            var results = _context.ExecuteQuery<T>(
                SqlGenerator.GenerateSelectAll(MappingCache.GetMapping<T>())
            ).ToList();

            if (_context.ObjectTrackingEnabled)
            {
                var mapping = MappingCache.GetMapping<T>();
                foreach (var entity in results)
                    _changeTracker.TrackLoaded(entity, mapping);
            }

            return results;
        }

        private static DynamicParameters ToDynamicParameters(IDictionary<string, object> parameters)
        {
            var dp = new DynamicParameters();
            foreach (var kv in parameters)
                dp.Add(kv.Key, kv.Value);
            return dp;
        }

        #endregion
    }
}
