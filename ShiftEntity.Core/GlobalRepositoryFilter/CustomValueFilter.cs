using System;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.GlobalRepositoryFilter;

public class CustomValueFilter<Entity, TValue> : IGlobalRepositoryFilter
    where Entity : ShiftEntity<Entity>
    where TValue : class
{
    public Guid ID { get; }
    public bool Disabled { get; set; }

    private Expression<Func<CustomValueFilterContext<Entity, TValue>, bool>> KeySelector { get; }
    private Func<ValueTask<TValue>>? ValuesProvider { get; set; }

    public CustomValueFilter(Expression<Func<CustomValueFilterContext<Entity, TValue>, bool>> keySelector, Guid id)
    {
        this.ID = id;
        this.KeySelector = keySelector;
    }

    public CustomValueFilter<Entity, TValue> ValueProvider(Func<ValueTask<TValue>> valuesProvider)
    {
        this.ValuesProvider = valuesProvider;

        return this;
    }

    public async ValueTask<Expression<Func<T, bool>>?> GetFilterExpression<T>() where T : ShiftEntity<T>
    {
        if (this.KeySelector is not Expression<Func<CustomValueFilterContext<T, TValue>, bool>> typedFilter)
            throw new InvalidOperationException("Invalid filter expression.");

        TValue? value = null;

        if (ValuesProvider is not null)
            value = await ValuesProvider.Invoke();

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
                if (node.Member.Name == nameof(CustomValueFilterContext<Entity, object>.Entity))
                    return _entityParameter;

                if (node.Member.Name == nameof(CustomValueFilterContext<Entity, object>.CustomValue))
                    return _valueExpression;
            }

            // For all other members, continue visiting as normal.
            return base.VisitMember(node);
        }
    }
}