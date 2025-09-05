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
    public List<string>? ClaimValues { get; set; }
    
    public bool WildCardRead { get; set; }
    public bool WildCardWrite { get; set; }
    public bool WildCardDelete { get; set; }
    public bool WildCardMaxAccess { get; set; }

    public List<string>? ReadableTypeAuthValues { get; set; }
    public List<string>? WritableTypeAuthValues { get; set; }
    public List<string>? DeletableTypeAuthValues { get; set; }
    public List<string>? MaxAccessTypeAuthValues { get; set; }
    
    public RepositoryGlobalFilterContext(TEntity entity, TValue value)
    {
        Entity = entity;
        Value = value;
    }
}


public class RepositoryGlobalFilter<Entity, TValue> : IRepositoryGlobalFilter 
    where Entity : ShiftEntity<Entity>
    where TValue : class
{
    public DynamicAction? DynamicAction { get; set; }
    public List<string>? AccessibleKeys { get; set; }
    public string? ClaimId { get; set; }
    public Expression<Func<Entity, long?>>? CreatedByUserIDKeySelector { get; set; }
    public Expression<Func<RepositoryGlobalFilterContext<Entity, TValue>, bool>> KeySelector { get; set; }
    internal Func<TValue>? ValuesProvider { get; set; }

    public Type? DTOTypeForHashId { get; set; }
    public Type? TypeAuthDTOTypeForHashId { get; set; }
    public bool ShowNulls { get; set; }
    public string? ClaimIdForClaimValuesProvider { get; set; }
    public string? SelfClaimIdForTypeAuthValuesProvider { get; set; }

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

    public RepositoryGlobalFilter<Entity, TValue> CustomValueProvider(Func<TValue>? valuesProvider)
    {
        this.ValuesProvider = valuesProvider;

        return this;
    }

    public RepositoryGlobalFilter<Entity, TValue> SelfClaimValueProvider(string claimId)
    {
        this.ClaimIdForClaimValuesProvider = claimId;
        return this;
    }

    public RepositoryGlobalFilter<Entity, TValue> ClaimValuesProvider<HashIdDTO>(string claimId) where HashIdDTO : ShiftEntityDTOBase, new()
    {
        this.ClaimIdForClaimValuesProvider = claimId;
        this.DTOTypeForHashId = new HashIdDTO().GetType();
        return this;
    }

    public RepositoryGlobalFilter<Entity, TValue> TypeAuthValuesProvider<HashIdDTO>(DynamicAction dynamicAction, string? selfClaimId = null) where HashIdDTO : ShiftEntityDTOBase, new()
    {
        this.SelfClaimIdForTypeAuthValuesProvider = selfClaimId;
        this.DynamicAction = dynamicAction;
        this.TypeAuthDTOTypeForHashId = new HashIdDTO().GetType();
        return this;
    }

    //public RepositoryGlobalFilter<Entity, TValue> DynamicActionValueProvider(DynamicAction dynamicAction)
    //{
    //    this.DynamicAction = dynamicAction;

    //    return this;
    //}

    private (List<string>? AccessibleIds, bool WildCard) GetTypeAuthValues(DynamicAction? dynamicAction, Access access, Type? dtoTypeForHashId, string[]? selfIds)
    {
        if (dynamicAction is not null)
        {
            var accessibleItems = this.TypeAuthService!.GetAccessibleItems(
                dynamicAction,
                x => x == access,
                selfIds
            );

            if (accessibleItems.AccessibleIds is not null && dtoTypeForHashId is not null)
            {
                accessibleItems.AccessibleIds = accessibleItems
                    .AccessibleIds
                    .Select(x => ShiftEntityHashIdService.Decode(x, dtoTypeForHashId).ToString())
                    .ToList();
            }

            return (accessibleItems.AccessibleIds, accessibleItems.WildCard);
        }

        return (null, false);
    }

    Expression<Func<T, bool>>? IRepositoryGlobalFilter.GetFilterExpression<T>()
    {
        if (this.KeySelector is not Expression<Func<RepositoryGlobalFilterContext<T, TValue>, bool>> typedFilter)
            throw new InvalidOperationException("Invalid filter expression.");

        TValue? value = null;

        if (ValuesProvider is not null)
            value = ValuesProvider.Invoke();

        List<string>? claimValues = null; // Hardcoded example for the string

        var wildCardRead = false;
        var wildCardWrite = false;
        var wildCardDelete = false;
        var wildCardMaxAccess = false;
        
        List<string>? readableTypeAuthValues = null;
        List<string>? writableTypeAuthValues = null;
        List<string>? deletableTypeAuthValues = null;
        List<string>? maxAccessTypeAuthValues = null;

        if (this.ClaimIdForClaimValuesProvider is not null)
        {
            var user = this.CurrentUserProvider?.GetUser();

            claimValues = user?
                .Claims?
                .Where(x => x.Type == this.ClaimIdForClaimValuesProvider)?
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
            string[]? selfIds = null;

            if (this.SelfClaimIdForTypeAuthValuesProvider is not null)
            {
                var user = this.CurrentUserProvider?.GetUser();

                selfIds = user?
                   .Claims?
                   .Where(x => x.Type == this.SelfClaimIdForTypeAuthValuesProvider)?
                   .Select(x => x.Value)
                   .ToArray();
            }

            (readableTypeAuthValues, wildCardRead) = GetTypeAuthValues(this.DynamicAction, Access.Read, this.TypeAuthDTOTypeForHashId, selfIds);
            (writableTypeAuthValues, wildCardWrite) = GetTypeAuthValues(this.DynamicAction, Access.Write, this.TypeAuthDTOTypeForHashId, selfIds);
            (deletableTypeAuthValues, wildCardDelete) = GetTypeAuthValues(this.DynamicAction, Access.Delete, this.TypeAuthDTOTypeForHashId, selfIds);
            (maxAccessTypeAuthValues, wildCardMaxAccess) = GetTypeAuthValues(this.DynamicAction, Access.Maximum, this.TypeAuthDTOTypeForHashId, selfIds);
        }

        var entityParam = Expression.Parameter(typeof(T), "entity");
        var valueExpr = Expression.Constant(value, typeof(TValue));
        var claimValuesExpression = Expression.Constant(claimValues, typeof(List<string>));

        var wildCardReadExpr = Expression.Constant(wildCardRead, typeof(bool));
        var wildCardWriteExpr = Expression.Constant(wildCardWrite, typeof(bool));
        var wildCardDeleteExpr = Expression.Constant(wildCardDelete, typeof(bool));
        var wildCardMaxAccessExpr = Expression.Constant(wildCardMaxAccess, typeof(bool));

        var readableTypeAuthValuesExpression = Expression.Constant(readableTypeAuthValues, typeof(List<string>));
        var writableTypeAuthValuesExpression = Expression.Constant(writableTypeAuthValues, typeof(List<string>));
        var deletableTypeAuthValuesExpression = Expression.Constant(deletableTypeAuthValues, typeof(List<string>));
        var maxAccessTypeAuthValuesExpression = Expression.Constant(maxAccessTypeAuthValues, typeof(List<string>));

        // Create the new visitor with all the necessary expressions.
        var visitor = new FilterExpressionVisitor<T, TValue>(
            typedFilter.Parameters[0],
            entityParam,
            valueExpr,
            claimValuesExpression,

            wildCardReadExpr,
            wildCardWriteExpr,
            wildCardDeleteExpr,
            wildCardMaxAccessExpr,

            readableTypeAuthValuesExpression,
            writableTypeAuthValuesExpression,
            deletableTypeAuthValuesExpression,
            maxAccessTypeAuthValuesExpression
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
        private readonly Expression _claimValuesExpression;
        
        private readonly Expression _wildcardReadExpression;
        private readonly Expression _wildcardWriteExpression;
        private readonly Expression _wildcardDeleteExpression;
        private readonly Expression _wildcardMaxAccessExpression;

        private readonly Expression _readableTypeAuthValuesExpression;
        private readonly Expression _writableTypeAuthValuesExpression;
        private readonly Expression _deletableTypeAuthValuesExpression;
        private readonly Expression _maxAccessTypeAuthValuesExpression;

        public FilterExpressionVisitor(
            ParameterExpression oldParameter, 
            ParameterExpression entityParameter, 
            Expression valueExpression, 
            Expression claimValuesExpression,
            
            Expression wildcardReadExpression, 
            Expression wildcardWriteExpression, 
            Expression wildcardDeleteExpression, 
            Expression wildcardMaxAccessExpression, 

            Expression readableTypeAuthValuesExpression,
            Expression writableTypeAuthValuesExpression,
            Expression deletableTypeAuthValuesExpression,
            Expression maxAccessTypeAuthValuesExpression
        )
        {
            _oldParameter = oldParameter;
            _entityParameter = entityParameter;
            _valueExpression = valueExpression;
            _claimValuesExpression = claimValuesExpression;
            
            _wildcardReadExpression = wildcardReadExpression;
            _wildcardWriteExpression = wildcardWriteExpression;
            _wildcardDeleteExpression = wildcardDeleteExpression;
            _wildcardMaxAccessExpression = wildcardMaxAccessExpression;

            _readableTypeAuthValuesExpression = readableTypeAuthValuesExpression;
            _writableTypeAuthValuesExpression = writableTypeAuthValuesExpression;
            _deletableTypeAuthValuesExpression = deletableTypeAuthValuesExpression;
            _maxAccessTypeAuthValuesExpression = maxAccessTypeAuthValuesExpression;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            // Check if the member access is on the old parameter
            if (node.Expression == _oldParameter)
            {
                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.Entity))
                    return _entityParameter;

                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.Value))
                    return _valueExpression;

                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.ClaimValues))
                    return _claimValuesExpression;


                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.WildCardRead))
                    return _wildcardReadExpression;
                
                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.WildCardWrite))
                    return _wildcardWriteExpression;
                
                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.WildCardDelete))
                    return _wildcardDeleteExpression;
                
                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.WildCardMaxAccess))
                    return _wildcardMaxAccessExpression;



                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.ReadableTypeAuthValues))
                    return _readableTypeAuthValuesExpression;
                
                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.WritableTypeAuthValues))
                    return _writableTypeAuthValuesExpression;
                
                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.DeletableTypeAuthValues))
                    return _deletableTypeAuthValuesExpression;
                
                if (node.Member.Name == nameof(RepositoryGlobalFilterContext<Entity, object>.MaxAccessTypeAuthValues))
                    return _maxAccessTypeAuthValuesExpression;
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