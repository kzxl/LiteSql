using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using Dapper;
using LiteSql.Mapping;
using LiteSql.Sql;

namespace LiteSql
{
    /// <summary>
    /// Represents a LINQ-style join query between two tables.
    /// Supports INNER JOIN and LEFT JOIN operations.
    /// </summary>
    public class JoinQuery<T1, T2, TResult> where T1 : class where T2 : class
    {
        private readonly IDbConnection _connection;
        private readonly IDbTransaction _transaction;
        private readonly EntityMapping _mapping1;
        private readonly EntityMapping _mapping2;
        private readonly Expression<Func<T1, object>> _outerKeySelector;
        private readonly Expression<Func<T2, object>> _innerKeySelector;
        private readonly Expression<Func<T1, T2, TResult>> _resultSelector;
        private readonly JoinType _joinType;
        private string _whereClause;
        private IDictionary<string, object> _whereParameters;

        internal JoinQuery(
            IDbConnection connection,
            IDbTransaction transaction,
            Expression<Func<T1, object>> outerKeySelector,
            Expression<Func<T2, object>> innerKeySelector,
            Expression<Func<T1, T2, TResult>> resultSelector,
            JoinType joinType)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _transaction = transaction;
            _mapping1 = MappingCache.GetMapping<T1>();
            _mapping2 = MappingCache.GetMapping<T2>();
            _outerKeySelector = outerKeySelector ?? throw new ArgumentNullException(nameof(outerKeySelector));
            _innerKeySelector = innerKeySelector ?? throw new ArgumentNullException(nameof(innerKeySelector));
            _resultSelector = resultSelector ?? throw new ArgumentNullException(nameof(resultSelector));
            _joinType = joinType;
        }

        /// <summary>
        /// Filters the join results with a WHERE clause.
        /// </summary>
        public JoinQuery<T1, T2, TResult> Where(Expression<Func<TResult, bool>> predicate)
        {
            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));

            // For now, we'll build a simple WHERE clause
            // In a full implementation, this would need to handle the result selector properly
            throw new NotImplementedException("Where clause on join results is not yet implemented. Use Where on individual tables before joining.");
        }

        /// <summary>
        /// Executes the join query and returns the results.
        /// </summary>
        public List<TResult> ToList()
        {
            var builder = new JoinBuilder(_mapping1, _mapping2);
            var (sql, parameters) = builder.BuildJoinQuery(
                _outerKeySelector,
                _innerKeySelector,
                _resultSelector,
                _joinType,
                _whereClause,
                _whereParameters);

            return _connection.Query<TResult>(sql, parameters, _transaction).ToList();
        }

        /// <summary>
        /// Returns the first result or throws an exception if no results.
        /// </summary>
        public TResult First()
        {
            var results = ToList();
            if (results.Count == 0)
                throw new InvalidOperationException("Sequence contains no elements");
            return results[0];
        }

        /// <summary>
        /// Returns the first result or default if no results.
        /// </summary>
        public TResult FirstOrDefault()
        {
            var results = ToList();
            return results.Count > 0 ? results[0] : default(TResult);
        }

        /// <summary>
        /// Returns the SQL query for debugging purposes.
        /// </summary>
        public string ToSql()
        {
            var builder = new JoinBuilder(_mapping1, _mapping2);
            var (sql, _) = builder.BuildJoinQuery(
                _outerKeySelector,
                _innerKeySelector,
                _resultSelector,
                _joinType,
                _whereClause,
                _whereParameters);
            return sql;
        }
    }

    /// <summary>
    /// Type of join operation.
    /// </summary>
    public enum JoinType
    {
        Inner,
        Left
    }

    /// <summary>
    /// Extension methods for creating join queries.
    /// </summary>
    public static class JoinExtensions
    {
        /// <summary>
        /// Performs an INNER JOIN between two tables.
        /// </summary>
        public static JoinQuery<T1, T2, TResult> Join<T1, T2, TKey, TResult>(
            this Table<T1> outer,
            Table<T2> inner,
            Expression<Func<T1, TKey>> outerKeySelector,
            Expression<Func<T2, TKey>> innerKeySelector,
            Expression<Func<T1, T2, TResult>> resultSelector)
            where T1 : class
            where T2 : class
        {
            if (outer == null) throw new ArgumentNullException(nameof(outer));
            if (inner == null) throw new ArgumentNullException(nameof(inner));

            // Convert TKey to object for internal use
            Expression<Func<T1, object>> outerKey = Expression.Lambda<Func<T1, object>>(
                Expression.Convert(outerKeySelector.Body, typeof(object)),
                outerKeySelector.Parameters);

            Expression<Func<T2, object>> innerKey = Expression.Lambda<Func<T2, object>>(
                Expression.Convert(innerKeySelector.Body, typeof(object)),
                innerKeySelector.Parameters);

            return new JoinQuery<T1, T2, TResult>(
                outer.Connection,
                outer.Transaction,
                outerKey,
                innerKey,
                resultSelector,
                JoinType.Inner);
        }

        /// <summary>
        /// Performs a LEFT JOIN between two tables.
        /// </summary>
        public static JoinQuery<T1, T2, TResult> LeftJoin<T1, T2, TKey, TResult>(
            this Table<T1> outer,
            Table<T2> inner,
            Expression<Func<T1, TKey>> outerKeySelector,
            Expression<Func<T2, TKey>> innerKeySelector,
            Expression<Func<T1, T2, TResult>> resultSelector)
            where T1 : class
            where T2 : class
        {
            if (outer == null) throw new ArgumentNullException(nameof(outer));
            if (inner == null) throw new ArgumentNullException(nameof(inner));

            Expression<Func<T1, object>> outerKey = Expression.Lambda<Func<T1, object>>(
                Expression.Convert(outerKeySelector.Body, typeof(object)),
                outerKeySelector.Parameters);

            Expression<Func<T2, object>> innerKey = Expression.Lambda<Func<T2, object>>(
                Expression.Convert(innerKeySelector.Body, typeof(object)),
                innerKeySelector.Parameters);

            return new JoinQuery<T1, T2, TResult>(
                outer.Connection,
                outer.Transaction,
                outerKey,
                innerKey,
                resultSelector,
                JoinType.Left);
        }
    }
}
