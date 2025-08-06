using Newtonsoft.Json.Linq;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Web;

public interface IDynamicActionFilter
{
    DynamicAction? DynamicAction { get; set; }

    public List<string>? AccessibleKeys { get; set; }

    //public Expression<Func<Entity, long?>>? CreatedByUserIDKeySelector { get; set; }

    public Type? DTOTypeForHashId { get; set; }
    public bool ShowNulls { get; set; }
    public string? SelfClaimId { get; set; }

    public string? ClaimId { get; set; }
    //Expression GetKeySelectorExpression();
    //ConstantExpression? GetCustomValueProviderExpression();
    Expression<Func<T, bool>>? GetFilterExpression<T>() where T : ShiftEntity<T>;
}

public class DynamicActionFilter<Entity, TValue> : IDynamicActionFilter where Entity : ShiftEntity<Entity>
{
    public DynamicAction? DynamicAction { get; set; }
    public List<string>? AccessibleKeys { get; set; }
    public string? ClaimId { get; set; }
    //public ParameterExpression ParameterExpression { get; set; }
    //public InvocationExpression InvocationExpression { get; set; }
    //public Type TKey { get; set; }
    public Expression<Func<Entity, long?>>? CreatedByUserIDKeySelector { get; set; }
    public Expression<Func<FilterContext<Entity, TValue>, bool>> KeySelector { get; set; }
    public Func<IServiceProvider, Entity, TValue>? ValuesProvider { get; set; }

    public Type? DTOTypeForHashId { get; set; }
    public bool ShowNulls { get; set; }
    public string? SelfClaimId { get; set; }

    public DynamicActionFilter(Expression<Func<FilterContext<Entity, TValue>, bool>> keySelector)
    {
        this.KeySelector = keySelector;
    }

    //public DynamicActionFilter(List<string> accessibleKeys, InvocationExpression invocationExpression, ParameterExpression parameterExpression, Type tKey)
    //{
    //    this.AccessibleKeys = accessibleKeys;
    //    this.TKey = tKey;
    //}

    //public DynamicActionFilter(string claimId, InvocationExpression invocationExpression, ParameterExpression parameterExpression, Type tKey)
    //{
    //    this.ClaimId = claimId;
    //    this.TKey = tKey;
    //}

    //public Expression GetKeySelectorExpression()
    //{
    //    return this.KeySelector;
    //}

    //public ConstantExpression? GetCustomValueProviderExpression()
    //{
    //    if(this.ValuesProvider == null)
    //        return null;

    //    return Expression.Constant(this.ValuesProvider.Invoke(null, null), typeof(TValue));
    //}

    public DynamicActionFilter<Entity, TValue> CustomValueProvider(Func<IServiceProvider, Entity, TValue>? valuesProvider)
    {
        this.ValuesProvider = valuesProvider;

        return this;
    }

    public DynamicActionFilter<Entity, TValue> DynamicActionValueProvider(DynamicAction dynamicAction)
    {
        this.DynamicAction = dynamicAction;

        return this;
    }


    Expression<Func<T, bool>>? IDynamicActionFilter.GetFilterExpression<T>()
    {
        if (this.KeySelector is not Expression<Func<FilterContext<T, TValue>, bool>> typedFilter)
            throw new InvalidOperationException("Invalid filter expression.");

        // Assuming ValuesProvider now returns a tuple or a custom object.
        // For this example, let's just create a hardcoded boolean for simplicity.
        var value = ValuesProvider.Invoke(null, null); // Your original list
        var wildCardValue = false; // Hardcoded example for the boolean

        var entityParam = Expression.Parameter(typeof(T), "entity");
        var valueExpr = Expression.Constant(value, typeof(TValue));
        var wildCardExpr = Expression.Constant(wildCardValue, typeof(bool));

        // Create the new visitor with all the necessary expressions.
        var visitor = new FilterExpressionVisitor<T, TValue>(
            typedFilter.Parameters[0],
            entityParam,
            valueExpr,
            wildCardExpr);

        // Visit the body of the original expression to replace the members.
        var newBody = visitor.Visit(typedFilter.Body);

        // Create and return the new lambda expression with the modified body.
        return Expression.Lambda<Func<T, bool>>(newBody, entityParam);
    }

    public class FilterExpressionVisitor<T, TValue> : ExpressionVisitor
    {
        private readonly ParameterExpression _oldParameter;
        private readonly ParameterExpression _entityParameter;
        private readonly Expression _valueExpression;
        private readonly Expression _wildcardExpression;

        public FilterExpressionVisitor(ParameterExpression oldParameter, ParameterExpression entityParameter, Expression valueExpression, Expression wildcardExpression)
        {
            _oldParameter = oldParameter;
            _entityParameter = entityParameter;
            _valueExpression = valueExpression;
            _wildcardExpression = wildcardExpression;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            // Check if the member access is on the old parameter
            if (node.Expression == _oldParameter)
            {
                if (node.Member.Name == "Entity")
                {
                    // If it's x.Entity, replace with the new entity parameter.
                    return _entityParameter;
                }
                if (node.Member.Name == "Value")
                {
                    // If it's x.Value, replace with our constant value expression.
                    return _valueExpression;
                }
                if (node.Member.Name == "WildCard")
                {
                    // If it's x.WildCard, replace with our constant wildcard expression.
                    return _wildcardExpression;
                }
            }

            // For all other members, continue visiting as normal.
            return base.VisitMember(node);
        }
    }

    //public DynamicActionFilter<Entity> IncludeNulls()
    //{
    //    this.ShowNulls = true;

    //    return this;
    //}

    //public DynamicActionFilter<Entity> IncludeCreatedByCurrentUser(Expression<Func<Entity, long?>>? keySelector)
    //{
    //    this.CreatedByUserIDKeySelector = keySelector;

    //    return this;
    //}

    //public DynamicActionFilter<Entity> IncludeSelfItems(string selfClaimId)
    //{
    //    this.SelfClaimId = selfClaimId;

    //    return this;
    //}

    //public DynamicActionFilter<Entity> DecodeHashId<DTO>()
    //{
    //    this.DTOTypeForHashId = typeof(DTO);

    //    return this;
    //}
}
