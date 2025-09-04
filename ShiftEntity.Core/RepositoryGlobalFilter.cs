using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Claims;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IRepositoryGlobalFilter
{
    //DynamicAction? DynamicAction { get; set; }
    //public List<string>? AccessibleKeys { get; set; }

    //public Expression<Func<Entity, long?>>? CreatedByUserIDKeySelector { get; set; }

    //public Type? DTOTypeForHashId { get; set; }
    //public bool ShowNulls { get; set; }
    //public string? SelfClaimId { get; set; }
    //public string? ClaimId { get; set; }
    Expression<Func<T, bool>>? GetFilterExpression<T>() where T : ShiftEntity<T>;
}

public class RepositoryGlobalFilterContext<TEntity, TValue> where TEntity : ShiftEntity<TEntity>
{
    public TEntity Entity { get; }
    public TValue Value { get; }
    public bool WildCard { get; set; }
    public List<string>? ClaimValues { get; set; }
    public List<string>? TypeAuthValues { get; set; }
    public RepositoryGlobalFilterContext(TEntity entity, TValue value)
    {
        Entity = entity;
        Value = value;
    }
}

public class GlobalFilterDataProviderOptions<TEntity> where TEntity : ShiftEntity<TEntity>
{
    public IServiceProvider ServiceProvider { get; set; } = default!;

    public GlobalFilterDataProviderOptions(IServiceProvider serviceProvider, TEntity entity)
    {
        ServiceProvider = serviceProvider;
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
    public Type? TypeAuthDTOTypeForHashId { get; set; }
    public bool ShowNulls { get; set; }
    public string? SelfClaimId { get; set; }

    private ICurrentUserProvider? CurrentUserProvider;
    private ITypeAuthService? TypeAuthService;

    public RepositoryGlobalFilter(
            Expression<Func<RepositoryGlobalFilterContext<Entity, TValue>, bool>> keySelector, 
            ICurrentUserProvider? currentUserProvider, 
            ITypeAuthService? TypeAuthService
    )
    {
        this.KeySelector = keySelector;
        this.CurrentUserProvider = currentUserProvider;
        this.TypeAuthService = TypeAuthService;
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

    public RepositoryGlobalFilter<Entity, TValue> SelfClaimValueProvider(string claimId)
    {
        this.SelfClaimId = claimId;
        return this;
    }

    public RepositoryGlobalFilter<Entity, TValue> ClaimValuesProvider<HashIdDTO>(string claimId) where HashIdDTO : ShiftEntityDTOBase, new()
    {
        this.SelfClaimId = claimId;
        this.DTOTypeForHashId = new HashIdDTO().GetType();
        return this;
    }

    public RepositoryGlobalFilter<Entity, TValue> TypeAuthValuesProvider<HashIdDTO>(DynamicAction dynamicAction) where HashIdDTO : ShiftEntityDTOBase, new()
    {
        this.DynamicAction = dynamicAction;
        this.TypeAuthDTOTypeForHashId = new HashIdDTO().GetType();
        return this;
    }

    //public RepositoryGlobalFilter<Entity, TValue> DynamicActionValueProvider(DynamicAction dynamicAction)
    //{
    //    this.DynamicAction = dynamicAction;

    //    return this;
    //}

    Expression<Func<T, bool>>? IRepositoryGlobalFilter.GetFilterExpression<T>()
    {
        if (this.KeySelector is not Expression<Func<RepositoryGlobalFilterContext<T, TValue>, bool>> typedFilter)
            throw new InvalidOperationException("Invalid filter expression.");

        // Assuming ValuesProvider now returns a tuple or a custom object.
        // For this example, let's just create a hardcoded boolean for simplicity.
        var value = ValuesProvider.Invoke(new GlobalFilterDataProviderOptions<Entity>(null, null)); // Your original list
        var wildCardValue = false; // Hardcoded example for the boolean
        List<string>? claimValues = null; // Hardcoded example for the string
        List<string>? typeAuthValues = null; // Hardcoded example for the string

        if (this.SelfClaimId is not null)
        {
            var user = this.CurrentUserProvider?.GetUser();

            claimValues = user?
                .Claims?
                .Where(x => x.Type == this.SelfClaimId)?
                .Select(x => x.Value)?
                .ToList();

            if (claimValues is not null && DTOTypeForHashId is not null)
            {
                claimValues = claimValues
                    .Select(x => ShiftEntityHashIdService.Decode(x, DTOTypeForHashId).ToString())
                    .ToList();
            }
        }

        if (this.DynamicAction is not null)
        {
            var user = this.CurrentUserProvider?.GetUser();

            typeAuthValues = user?
                .Claims?
                .Where(x => x.Type == this.SelfClaimId)?
                .Select(x => x.Value)?
                .ToList();

            var accessibleItems = this.TypeAuthService!.GetAccessibleItems(this.DynamicAction, x => x == Access.Read, typeAuthValues?.ToArray());

            if (accessibleItems.AccessibleIds is not null && TypeAuthDTOTypeForHashId is not null)
            {
                accessibleItems.AccessibleIds = accessibleItems
                    .AccessibleIds
                    .Select(x => ShiftEntityHashIdService.Decode(x, TypeAuthDTOTypeForHashId).ToString())
                    .ToList();
            }

            if (accessibleItems.WildCard)
                wildCardValue = true;

            typeAuthValues = accessibleItems.AccessibleIds;
        }

        var entityParam = Expression.Parameter(typeof(T), "entity");
        var valueExpr = Expression.Constant(value, typeof(TValue));
        var wildCardExpr = Expression.Constant(wildCardValue, typeof(bool));
        var claimValuesExpression = Expression.Constant(claimValues, typeof(List<string>));
        var typeAuthValuesExpression = Expression.Constant(typeAuthValues, typeof(List<string>));

        // Create the new visitor with all the necessary expressions.
        var visitor = new FilterExpressionVisitor<T, TValue>(
            typedFilter.Parameters[0],
            entityParam,
            valueExpr,
            wildCardExpr,
            claimValuesExpression,
            typeAuthValuesExpression
        );

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
        private readonly Expression _claimValuesExpression;
        private readonly Expression _typeAuthValuesExpression;

        public FilterExpressionVisitor(ParameterExpression oldParameter, ParameterExpression entityParameter, Expression valueExpression, Expression wildcardExpression, Expression claimValuesExpression, Expression typeAuthValuesExpression)
        {
            _oldParameter = oldParameter;
            _entityParameter = entityParameter;
            _valueExpression = valueExpression;
            _wildcardExpression = wildcardExpression;
            _claimValuesExpression = claimValuesExpression;
            _typeAuthValuesExpression = typeAuthValuesExpression;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            // Check if the member access is on the old parameter
            if (node.Expression == _oldParameter)
            {
                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.Entity))
                {
                    // If it's x.Entity, replace with the new entity parameter.
                    return _entityParameter;
                }
                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.Value))
                {
                    // If it's x.Value, replace with our constant value expression.
                    return _valueExpression;
                }
                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.WildCard))
                {
                    // If it's x.WildCard, replace with our constant wildcard expression.
                    return _wildcardExpression;
                }
                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.ClaimValues))
                {
                    return _claimValuesExpression;
                }
                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.TypeAuthValues))
                {
                    return _typeAuthValuesExpression;
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

public class TypeAuthGlobalFilter : IRepositoryGlobalFilter
{
    public DynamicAction DynamicAction { get; set; }
    public Type? HashedIdDTOType {  get; set; }

    public TypeAuthGlobalFilter(DynamicAction dynamicAction)
    {
        this.DynamicAction = dynamicAction;
    }

    public TypeAuthGlobalFilter(DynamicAction dynamicAction, string claimid)
    {
        this.DynamicAction = dynamicAction;
    }

    public TypeAuthGlobalFilter(DynamicAction dynamicAction, Type hashedIdDTOType)
    {
        this.DynamicAction = dynamicAction;
        this.HashedIdDTOType = hashedIdDTOType;
    }

    public TypeAuthGlobalFilter(DynamicAction dynamicAction, Type hashedIdDTOType, string claimId)
    {
        this.DynamicAction = dynamicAction;
        this.HashedIdDTOType = hashedIdDTOType;
    }

    public TypeAuthGlobalFilter SelfClaimValueProvider(string claimId)
    {
        return this;
    }

    Expression<Func<T, bool>>? IRepositoryGlobalFilter.GetFilterExpression<T>()
    {
        return null;
        //if (this.KeySelector is not Expression<Func<RepositoryGlobalFilterContext<T, TValue>, bool>> typedFilter)
        //    throw new InvalidOperationException("Invalid filter expression.");

        //// Assuming ValuesProvider now returns a tuple or a custom object.
        //// For this example, let's just create a hardcoded boolean for simplicity.
        //var value = ValuesProvider.Invoke(new GlobalFilterDataProviderOptions<Entity>(null, null)); // Your original list
        //var wildCardValue = false; // Hardcoded example for the boolean

        //var entityParam = Expression.Parameter(typeof(T), "entity");
        //var valueExpr = Expression.Constant(value, typeof(TValue));
        //var wildCardExpr = Expression.Constant(wildCardValue, typeof(bool));

        //// Create the new visitor with all the necessary expressions.
        //var visitor = new FilterExpressionVisitor<T, TValue>(
        //    typedFilter.Parameters[0],
        //    entityParam,
        //    valueExpr,
        //    wildCardExpr);

        //// Visit the body of the original expression to replace the members.
        //var newBody = visitor.Visit(typedFilter.Body);

        //// Create and return the new lambda expression with the modified body.
        //return Expression.Lambda<Func<T, bool>>(newBody, entityParam);
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