using LiteSql.Mapping;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace LiteSql.Sql
{
    /// <summary>
    /// Translates LINQ Expression trees (lambda predicates) into SQL WHERE clauses
    /// with parameterized values for use with Dapper.
    /// 
    /// Supported expressions:
    /// - Binary: ==, !=, &lt;, &lt;=, &gt;, &gt;=
    /// - Logical: &amp;&amp;, ||
    /// - Unary: ! (not)
    /// - Member access: entity.Property
    /// - Constants and closures
    /// - null checks: x == null → IS NULL, x != null → IS NOT NULL
    /// - String: Contains, StartsWith, EndsWith
    /// - Collection: list.Contains(x) → IN (...)
    /// </summary>
    public class WhereBuilder
    {
        private readonly EntityMapping _mapping;
        private readonly IDictionary<string, object> _parameters = new Dictionary<string, object>();
        private int _paramIndex;
        private string _paramPrefix = "w";

        public WhereBuilder(EntityMapping mapping)
        {
            _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        }

        /// <summary>
        /// Translates a lambda expression into a SQL WHERE clause.
        /// Returns the WHERE clause (without "WHERE" keyword) and parameters.
        /// </summary>
        public (string Sql, IDictionary<string, object> Parameters) Build<T>(
            Expression<Func<T, bool>> predicate) where T : class
        {
            _parameters.Clear();
            _paramIndex = 0;

            var sql = Visit(predicate.Body);
            return (sql, _parameters);
        }

        /// <summary>
        /// Translates a non-generic LambdaExpression into SQL WHERE clause.
        /// Used by global query filters.
        /// </summary>
        public (string Sql, IDictionary<string, object> Parameters) BuildFromLambda(
            LambdaExpression predicate)
        {
            // Use @f prefix for filter params to avoid collision with @w user params
            // Don't reset _paramIndex — allows multiple sequential calls to produce unique names
            _paramPrefix = "f";

            var sql = Visit(predicate.Body);
            _paramPrefix = "w"; // Reset for next use
            return (sql, _parameters.Count > 0 ? new Dictionary<string, object>(_parameters) : null);
        }

        private string Visit(Expression expression)
        {
            switch (expression)
            {
                case BinaryExpression binary:
                    return VisitBinary(binary);

                case UnaryExpression unary:
                    return VisitUnary(unary);

                case MemberExpression member:
                    return VisitMember(member);

                case ConstantExpression constant:
                    return VisitConstant(constant);

                case MethodCallExpression method:
                    return VisitMethodCall(method);

                default:
                    throw new NotSupportedException(
                        $"Expression type '{expression.NodeType}' is not supported in WHERE clause.");
            }
        }

        private string VisitBinary(BinaryExpression binary)
        {
            // Handle null comparisons specially: x == null → IS NULL
            if (IsNullComparison(binary, out var nullMemberSql, out var isNullCheck))
            {
                return isNullCheck
                    ? $"{nullMemberSql} IS NULL"
                    : $"{nullMemberSql} IS NOT NULL";
            }

            var left = Visit(binary.Left);
            var right = Visit(binary.Right);

            string op;
            switch (binary.NodeType)
            {
                case ExpressionType.Equal: op = "="; break;
                case ExpressionType.NotEqual: op = "<>"; break;
                case ExpressionType.LessThan: op = "<"; break;
                case ExpressionType.LessThanOrEqual: op = "<="; break;
                case ExpressionType.GreaterThan: op = ">"; break;
                case ExpressionType.GreaterThanOrEqual: op = ">="; break;
                case ExpressionType.AndAlso: op = "AND"; break;
                case ExpressionType.OrElse: op = "OR"; break;
                default:
                    throw new NotSupportedException(
                        $"Binary operator '{binary.NodeType}' is not supported.");
            }

            // Wrap OR conditions in parentheses
            if (binary.NodeType == ExpressionType.OrElse)
                return $"({left} {op} {right})";

            return $"{left} {op} {right}";
        }

        private string VisitUnary(UnaryExpression unary)
        {
            if (unary.NodeType == ExpressionType.Not)
            {
                var operand = Visit(unary.Operand);
                return $"NOT ({operand})";
            }

            if (unary.NodeType == ExpressionType.Convert)
            {
                return Visit(unary.Operand);
            }

            throw new NotSupportedException(
                $"Unary operator '{unary.NodeType}' is not supported.");
        }

        private string VisitMember(MemberExpression member)
        {
            // Check if this is an entity property (e.g., p.Name)
            if (member.Expression is ParameterExpression)
            {
                var columnMapping = _mapping.Columns
                    .FirstOrDefault(c => c.Property.Name == member.Member.Name);

                if (columnMapping != null)
                    return $"[{columnMapping.ColumnName}]";

                // Fallback: use member name as column name
                return $"[{member.Member.Name}]";
            }

            // Otherwise, evaluate the expression to get its value
            var value = EvaluateExpression(member);
            return AddParameter(value);
        }

        private string VisitConstant(ConstantExpression constant)
        {
            if (constant.Value == null)
                return "NULL";

            return AddParameter(constant.Value);
        }

        private string VisitMethodCall(MethodCallExpression method)
        {
            // String methods: Contains, StartsWith, EndsWith
            if (method.Object != null && method.Object.Type == typeof(string))
            {
                var column = Visit(method.Object);
                var arg = EvaluateExpression(method.Arguments[0]);

                switch (method.Method.Name)
                {
                    case "Contains":
                        return $"{column} LIKE {AddParameter($"%{arg}%")}";
                    case "StartsWith":
                        return $"{column} LIKE {AddParameter($"{arg}%")}";
                    case "EndsWith":
                        return $"{column} LIKE {AddParameter($"%{arg}")}";
                }
            }

            // IEnumerable.Contains: list.Contains(x) → x IN (@p0, @p1, ...)
            if (method.Method.Name == "Contains")
            {
                // Static Enumerable.Contains(source, item) or List<T>.Contains(item)
                Expression collectionExpr, itemExpr;

                if (method.Object != null)
                {
                    // Instance method: list.Contains(x)
                    collectionExpr = method.Object;
                    itemExpr = method.Arguments[0];
                }
                else if (method.Arguments.Count == 2)
                {
                    // Static method: Enumerable.Contains(list, x)
                    collectionExpr = method.Arguments[0];
                    itemExpr = method.Arguments[1];
                }
                else
                {
                    throw new NotSupportedException("Unsupported Contains overload.");
                }

                var column = Visit(itemExpr);
                var collection = EvaluateExpression(collectionExpr) as IEnumerable;
                if (collection == null)
                    throw new InvalidOperationException("Contains target must be a collection.");

                var paramNames = new List<string>();
                foreach (var item in collection)
                {
                    paramNames.Add(AddParameter(item));
                }

                if (paramNames.Count == 0)
                    return "1 = 0"; // Empty collection → always false

                return $"{column} IN ({string.Join(", ", paramNames)})";
            }

            throw new NotSupportedException(
                $"Method '{method.Method.Name}' is not supported in WHERE clause.");
        }

        private bool IsNullComparison(BinaryExpression binary, out string memberSql, out bool isNull)
        {
            memberSql = null;
            isNull = false;

            if (binary.NodeType != ExpressionType.Equal && binary.NodeType != ExpressionType.NotEqual)
                return false;

            Expression memberExpr = null;

            if (IsNullExpression(binary.Right))
                memberExpr = binary.Left;
            else if (IsNullExpression(binary.Left))
                memberExpr = binary.Right;

            if (memberExpr == null)
                return false;

            memberSql = Visit(memberExpr);
            isNull = binary.NodeType == ExpressionType.Equal;
            return true;
        }

        private bool IsNullExpression(Expression expression)
        {
            if (expression is ConstantExpression constant && constant.Value == null)
                return true;

            // Handle nullable conversions: (object)null
            if (expression is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
                return IsNullExpression(unary.Operand);

            return false;
        }

        private string AddParameter(object value)
        {
            var paramName = $"@{_paramPrefix}{_paramIndex++}";
            _parameters[paramName] = value;
            return paramName;
        }

        /// <summary>
        /// Evaluates a constant or closure expression to get its runtime value.
        /// </summary>
        private object EvaluateExpression(Expression expression)
        {
            // Try fast path for common closure pattern: () => someLocal
            if (expression is MemberExpression member)
            {
                if (member.Expression is ConstantExpression closureConstant)
                {
                    var field = member.Member as FieldInfo;
                    if (field != null)
                        return field.GetValue(closureConstant.Value);

                    var prop = member.Member as PropertyInfo;
                    if (prop != null)
                        return prop.GetValue(closureConstant.Value);
                }
            }

            if (expression is ConstantExpression constant)
                return constant.Value;

            // Fallback: compile and invoke
            var lambda = Expression.Lambda(expression);
            return lambda.Compile().DynamicInvoke();
        }
    }
}
