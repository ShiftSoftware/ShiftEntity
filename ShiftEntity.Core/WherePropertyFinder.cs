using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.Core;

internal class WherePropertyFinder : ExpressionVisitor
{
    private readonly List<string> propertyPath;

    public bool FoundWhereOnProperty { get; private set; } = false;

    public WherePropertyFinder(LambdaExpression propertyExpression)
    {
        propertyPath = GetPropertyPath(propertyExpression.Body);
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // Early exit if already found
        if (FoundWhereOnProperty)
            return node;

        if (node.Method.DeclaringType == typeof(Queryable) && node.Method.Name == "Where")
        {
            if (node.Arguments.Count > 1)
            {
                var lambdaExpression = node.Arguments[1];

                if (lambdaExpression is UnaryExpression unaryExpression && unaryExpression.Operand is LambdaExpression lambda)
                {
                    var propertyFinder = new PropertyAccessFinder(propertyPath);
                    propertyFinder.Visit(lambda.Body);

                    if (propertyFinder.Found)
                    {
                        FoundWhereOnProperty = true;
                        return node;
                    }
                }
            }
        }
        return base.VisitMethodCall(node);
    }

    private static List<string> GetPropertyPath(Expression expression)
    {
        var path = new List<string>();
        while (true)
        {
            if (expression is MemberExpression member)
            {
                path.Insert(0, member.Member.Name);
                expression = member.Expression!;
            }
            else if (expression is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
            {
                expression = unary.Operand;
            }
            else
            {
                break;
            }
        }
        return path;
    }

    /// <summary>
    /// Inner visitor that traverses the lambda body to find property access.
    /// Stops as soon as the property is found.
    /// </summary>
    private class PropertyAccessFinder : ExpressionVisitor
    {
        private readonly List<string> _targetPath;
        public bool Found { get; private set; }

        public PropertyAccessFinder(List<string> targetPath)
        {
            _targetPath = targetPath;
        }

        public override Expression? Visit(Expression? node)
        {
            // Early exit - stop traversal once found
            if (Found || node == null)
                return node;

            return base.Visit(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (Found)
                return node;

            var path = GetMemberPath(node);
            if (path.SequenceEqual(_targetPath))
            {
                Found = true;
                return node;
            }

            return base.VisitMember(node);
        }

        private static List<string> GetMemberPath(MemberExpression expression)
        {
            var path = new List<string>();
            Expression? current = expression;

            while (current != null)
            {
                if (current is MemberExpression member)
                {
                    path.Insert(0, member.Member.Name);
                    current = member.Expression;
                }
                else if (current is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
                {
                    current = unary.Operand;
                }
                else
                {
                    break;
                }
            }
            return path;
        }
    }
}
