using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using LiteSql.Mapping;

namespace LiteSql.Sql
{
    /// <summary>
    /// Builds SQL JOIN queries from LINQ expressions.
    /// Supports INNER JOIN and LEFT JOIN operations.
    /// </summary>
    public class JoinBuilder
    {
        private readonly EntityMapping _mapping1;
        private readonly EntityMapping _mapping2;
        private readonly IDictionary<string, object> _parameters;
        private int _paramIndex;

        public JoinBuilder(EntityMapping mapping1, EntityMapping mapping2)
        {
            _mapping1 = mapping1 ?? throw new ArgumentNullException(nameof(mapping1));
            _mapping2 = mapping2 ?? throw new ArgumentNullException(nameof(mapping2));
            _parameters = new Dictionary<string, object>();
        }

        /// <summary>
        /// Builds a complete JOIN SQL query.
        /// </summary>
        public (string Sql, IDictionary<string, object> Parameters) BuildJoinQuery<T1, T2, TResult>(
            Expression<Func<T1, object>> outerKeySelector,
            Expression<Func<T2, object>> innerKeySelector,
            Expression<Func<T1, T2, TResult>> resultSelector,
            JoinType joinType,
            string whereClause,
            IDictionary<string, object> whereParameters)
        {
            _parameters.Clear();
            _paramIndex = 0;

            // Copy WHERE parameters if present
            if (whereParameters != null)
            {
                foreach (var kvp in whereParameters)
                    _parameters[kvp.Key] = kvp.Value;
            }

            // Extract join keys
            var outerKey = ExtractColumnName(outerKeySelector.Body, _mapping1);
            var innerKey = ExtractColumnName(innerKeySelector.Body, _mapping2);

            // Build SELECT clause from result selector
            var selectClause = BuildSelectClause(resultSelector);

            // Build JOIN clause
            var joinKeyword = joinType == JoinType.Inner ? "INNER JOIN" : "LEFT JOIN";
            var joinClause = $"{joinKeyword} [{_mapping2.TableName}] AS t2 ON t1.[{outerKey}] = t2.[{innerKey}]";

            // Combine all parts
            var sql = new StringBuilder();
            sql.Append($"SELECT {selectClause}");
            sql.Append($" FROM [{_mapping1.TableName}] AS t1");
            sql.Append($" {joinClause}");

            if (!string.IsNullOrEmpty(whereClause))
                sql.Append($" WHERE {whereClause}");

            return (sql.ToString(), _parameters);
        }

        /// <summary>
        /// Extracts column name from a key selector expression.
        /// </summary>
        private string ExtractColumnName(Expression expr, EntityMapping mapping)
        {
            // Unwrap Convert if present
            if (expr is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
                expr = unary.Operand;

            if (expr is MemberExpression member && member.Expression is ParameterExpression)
            {
                var propName = member.Member.Name;
                var col = mapping.Columns.FirstOrDefault(c => c.Property.Name == propName);
                if (col == null)
                    throw new InvalidOperationException(
                        $"Property '{propName}' is not a mapped column of '{mapping.EntityType.Name}'.");
                return col.ColumnName;
            }

            throw new NotSupportedException(
                $"Join key expression '{expr}' must be a simple property access (e.g., x => x.Id).");
        }

        /// <summary>
        /// Builds SELECT clause from result selector expression.
        /// </summary>
        private string BuildSelectClause<T1, T2, TResult>(Expression<Func<T1, T2, TResult>> resultSelector)
        {
            var body = resultSelector.Body;
            var columns = new List<string>();

            if (body is NewExpression newExpr)
            {
                // Anonymous type: new { o.Id, c.Name }
                for (int i = 0; i < newExpr.Arguments.Count; i++)
                {
                    var arg = newExpr.Arguments[i];
                    var alias = newExpr.Members?[i]?.Name;

                    var columnSql = TranslateSelectArgument(arg, resultSelector.Parameters[0], resultSelector.Parameters[1]);

                    if (!string.IsNullOrEmpty(alias))
                        columns.Add($"{columnSql} AS [{alias}]");
                    else
                        columns.Add(columnSql);
                }
            }
            else if (body is MemberInitExpression initExpr)
            {
                // DTO: new DTO { OrderId = o.Id, CustomerName = c.Name }
                foreach (var binding in initExpr.Bindings)
                {
                    if (binding is MemberAssignment assignment)
                    {
                        var columnSql = TranslateSelectArgument(assignment.Expression, resultSelector.Parameters[0], resultSelector.Parameters[1]);
                        columns.Add($"{columnSql} AS [{binding.Member.Name}]");
                    }
                }
            }
            else
            {
                throw new NotSupportedException(
                    $"Result selector expression type '{body.NodeType}' is not supported. " +
                    $"Use anonymous types (new {{ }}) or DTOs (new DTO {{ }}).");
            }

            return string.Join(", ", columns);
        }

        /// <summary>
        /// Translates a single SELECT argument to SQL column reference.
        /// </summary>
        private string TranslateSelectArgument(Expression expr, ParameterExpression param1, ParameterExpression param2)
        {
            // Unwrap Convert if present
            if (expr is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
                expr = unary.Operand;

            if (expr is MemberExpression member && member.Expression is ParameterExpression param)
            {
                var propName = member.Member.Name;

                // Determine which table this property belongs to
                if (param.Name == param1.Name)
                {
                    var col = _mapping1.Columns.FirstOrDefault(c => c.Property.Name == propName);
                    if (col == null)
                        throw new InvalidOperationException(
                            $"Property '{propName}' is not a mapped column of '{_mapping1.EntityType.Name}'.");
                    return $"t1.[{col.ColumnName}]";
                }
                else if (param.Name == param2.Name)
                {
                    var col = _mapping2.Columns.FirstOrDefault(c => c.Property.Name == propName);
                    if (col == null)
                        throw new InvalidOperationException(
                            $"Property '{propName}' is not a mapped column of '{_mapping2.EntityType.Name}'.");
                    return $"t2.[{col.ColumnName}]";
                }
            }

            throw new NotSupportedException(
                $"Select argument '{expr}' is not supported. " +
                $"Use simple property access (e.g., o.Id, c.Name).");
        }
    }
}
