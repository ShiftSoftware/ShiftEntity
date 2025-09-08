using System;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.Core.RepositoryGlobalFilter;

public class CustomValueRepositoryGlobalFilter<Entity, TValue> : IRepositoryGlobalFilter
    where Entity : ShiftEntity<Entity>
    where TValue : class
{
    public Expression<Func<CustomValueRepositoryGlobalFilterContext<Entity, TValue>, bool>> KeySelector { get; set; }
    internal Func<TValue>? ValuesProvider { get; set; }
    public Guid ID { get; set; }
    public bool Disabled { get; set; }

    public CustomValueRepositoryGlobalFilter(Expression<Func<CustomValueRepositoryGlobalFilterContext<Entity, TValue>, bool>> keySelector)
    {
        this.KeySelector = keySelector;
    }
    
    public CustomValueRepositoryGlobalFilter<Entity, TValue> CustomValueProvider(Func<TValue>? valuesProvider)
    {
        this.ValuesProvider = valuesProvider;

        return this;
    }

    Expression<Func<T, bool>>? IRepositoryGlobalFilter.GetFilterExpression<T>()
    {
        if (this.KeySelector is not Expression<Func<CustomValueRepositoryGlobalFilterContext<T, TValue>, bool>> typedFilter)
            throw new InvalidOperationException("Invalid filter expression.");

        TValue? value = null;

        if (ValuesProvider is not null)
            value = ValuesProvider.Invoke();

        var entityParam = Expression.Parameter(typeof(T), "entity");
        var valueExpr = Expression.Constant(value, typeof(TValue));

        // Create the new visitor with all the necessary expressions.
        var visitor = new FilterExpressionVisitor<T>(
            typedFilter.Parameters[0],
            entityParam,
            valueExpr
        );

        // Visit the body of the original expression to replace the members.
        var newBody = visitor.Visit(typedFilter.Body);

        // Create and return the new lambda expression with the modified body.
        return Expression.Lambda<Func<T, bool>>(newBody, entityParam);
    }

    public class FilterExpressionVisitor<T> : ExpressionVisitor
    {
        private readonly ParameterExpression _oldParameter;
        private readonly ParameterExpression _entityParameter;
        private readonly Expression _valueExpression;
        public FilterExpressionVisitor(
            ParameterExpression oldParameter,
            ParameterExpression entityParameter,
            Expression valueExpression
        )
        {
            _oldParameter = oldParameter;
            _entityParameter = entityParameter;
            _valueExpression = valueExpression;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            // Check if the member access is on the old parameter
            if (node.Expression == _oldParameter)
            {
                if (node.Member.Name == nameof(CustomValueRepositoryGlobalFilterContext<Entity, object>.Entity))
                    return _entityParameter;

                if (node.Member.Name == nameof(CustomValueRepositoryGlobalFilterContext<Entity, object>.CustomValue))
                    return _valueExpression;
            }

            // For all other members, continue visiting as normal.
            return base.VisitMember(node);
        }
    }
}