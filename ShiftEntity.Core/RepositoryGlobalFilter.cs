using ShiftSoftware.TypeAuth.Core.Actions;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IRepositoryGlobalFilter
{
    DynamicAction? DynamicAction { get; set; }
    public List<string>? AccessibleKeys { get; set; }

    //public Expression<Func<Entity, long?>>? CreatedByUserIDKeySelector { get; set; }

    public Type? DTOTypeForHashId { get; set; }
    public bool ShowNulls { get; set; }
    public string? SelfClaimId { get; set; }
    public string? ClaimId { get; set; }
    Expression<Func<T, bool>>? GetFilterExpression<T>() where T : ShiftEntity<T>;
}

public class RepositoryGlobalFilterContext<TEntity, TValue> where TEntity : ShiftEntity<TEntity>
{
    public TEntity Entity { get; }
    public TValue Value { get; }
    public bool WildCard { get; set; }
    public RepositoryGlobalFilterContext(TEntity entity, TValue value)
    {
        Entity = entity;
        Value = value;
    }
}

public class GlobalFilterDataProviderOptions<TEntity> where TEntity: ShiftEntity<TEntity>
{
    public IServiceProvider ServiceProvider { get; set; } = default!;
    public TEntity Entity { get; set; } = default!;

    public GlobalFilterDataProviderOptions(IServiceProvider serviceProvider, TEntity entity)
    {
        ServiceProvider = serviceProvider;
        Entity = entity;
    }
}

public class RepositoryGlobalFilter<Entity, TValue> : IRepositoryGlobalFilter where Entity : ShiftEntity<Entity>
{
    public DynamicAction? DynamicAction { get; set; }
    public List<string>? AccessibleKeys { get; set; }
    public string? ClaimId { get; set; }
    public Expression<Func<Entity, long?>>? CreatedByUserIDKeySelector { get; set; }
    public Expression<Func<RepositoryGlobalFilterContext<Entity, TValue>, bool>> KeySelector { get; set; }
    internal Func<GlobalFilterDataProviderOptions<Entity>, TValue>? ValuesProvider { get; set; }

    public Type? DTOTypeForHashId { get; set; }
    public bool ShowNulls { get; set; }
    public string? SelfClaimId { get; set; }

    public RepositoryGlobalFilter(Expression<Func<RepositoryGlobalFilterContext<Entity, TValue>, bool>> keySelector)
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

    public RepositoryGlobalFilter<Entity, TValue> CustomValueProvider(Func<GlobalFilterDataProviderOptions<Entity>, TValue>? valuesProvider)
    {
        this.ValuesProvider = valuesProvider;

        return this;
    }

    public RepositoryGlobalFilter<Entity, TValue> DynamicActionValueProvider(DynamicAction dynamicAction)
    {
        this.DynamicAction = dynamicAction;

        return this;
    }

    Expression<Func<T, bool>>? IRepositoryGlobalFilter.GetFilterExpression<T>()
    {
        if (this.KeySelector is not Expression<Func<RepositoryGlobalFilterContext<T, TValue>, bool>> typedFilter)
            throw new InvalidOperationException("Invalid filter expression.");

        // Assuming ValuesProvider now returns a tuple or a custom object.
        // For this example, let's just create a hardcoded boolean for simplicity.
        var value = ValuesProvider.Invoke(new GlobalFilterDataProviderOptions<Entity>(null, null)); // Your original list
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

//public class DynamicActionFilterBy_New<Entity> where Entity : ShiftEntity<Entity>
//{
//    public DynamicAction? DynamicAction { get; set; }
//    public List<string>? AccessibleKeys { get; set; }
//    public string? ClaimId { get; set; }
//    public ParameterExpression ParameterExpression { get; set; }
//    public InvocationExpression InvocationExpression { get; set; }
//    public Type TKey { get; set; }
//    public Expression<Func<Entity, long?>>? CreatedByUserIDKeySelector { get; set; }
//    public Type? DTOTypeForHashId { get; set; }
//    public bool ShowNulls { get; set; }
//    public string? SelfClaimId { get; set; }

//    public DynamicActionFilterBy_New(DynamicAction dynamicAction, InvocationExpression invocationExpression, ParameterExpression parameterExpression, Type tKey)
//    {
//        this.DynamicAction = dynamicAction;
//        this.InvocationExpression = invocationExpression;
//        this.ParameterExpression = parameterExpression;
//        this.TKey = tKey;
//    }

//    public DynamicActionFilterBy_New(List<string> accessibleKeys, InvocationExpression invocationExpression, ParameterExpression parameterExpression, Type tKey)
//    {
//        this.AccessibleKeys = accessibleKeys;
//        this.InvocationExpression = invocationExpression;
//        this.ParameterExpression = parameterExpression;
//        this.TKey = tKey;
//    }

//    public DynamicActionFilterBy_New(string claimId, InvocationExpression invocationExpression, ParameterExpression parameterExpression, Type tKey)
//    {
//        this.ClaimId = claimId;
//        this.InvocationExpression = invocationExpression;
//        this.ParameterExpression = parameterExpression;
//        this.TKey = tKey;
//    }

//    public DynamicActionFilterBy_New<Entity> IncludeNulls()
//    {
//        this.ShowNulls = true;

//        return this;
//    }

//    public DynamicActionFilterBy_New<Entity> IncludeCreatedByCurrentUser(Expression<Func<Entity, long?>>? keySelector)
//    {
//        this.CreatedByUserIDKeySelector = keySelector;

//        return this;
//    }

//    public DynamicActionFilterBy_New<Entity> IncludeSelfItems(string selfClaimId)
//    {
//        this.SelfClaimId = selfClaimId;

//        return this;
//    }

//    public DynamicActionFilterBy_New<Entity> DecodeHashId<DTO>()
//    {
//        this.DTOTypeForHashId = typeof(DTO);

//        return this;
//    }
//}