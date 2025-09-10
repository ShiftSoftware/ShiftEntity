using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.Core.RepositoryGlobalFilter;

public class TypeAuthValuesRepositoryGlobalFilter<Entity> : IRepositoryGlobalFilter
    where Entity : ShiftEntity<Entity>
{
    public Guid ID { get; }
    public bool Disabled { get; set; }
    private  DynamicAction? DynamicAction { get; set; }
    private Expression<Func<TypeAuthValuesRepositoryGlobalFilterContext<Entity>, bool>> KeySelector { get; }

    private Type? TypeAuthDTOTypeForHashId { get; set; }
    private string? SelfClaimIdForTypeAuthValuesProvider { get; set; }

    private readonly ICurrentUserProvider? CurrentUserProvider;
    private readonly ITypeAuthService? TypeAuthService;

    public TypeAuthValuesRepositoryGlobalFilter(
            Expression<Func<TypeAuthValuesRepositoryGlobalFilterContext<Entity>, bool>> keySelector,
            ICurrentUserProvider? currentUserProvider,
            ITypeAuthService? TypeAuthService,
            Guid id
    )
    {
        this.KeySelector = keySelector;
        this.CurrentUserProvider = currentUserProvider;
        this.TypeAuthService = TypeAuthService;
        this.ID = id;
    }

    public TypeAuthValuesRepositoryGlobalFilter<Entity> ValueProvider(DynamicAction dynamicAction, string? selfClaimId = null)
    {
        this.SelfClaimIdForTypeAuthValuesProvider = selfClaimId;
        this.DynamicAction = dynamicAction;
        return this;
    }

    public TypeAuthValuesRepositoryGlobalFilter<Entity> ValueProvider<HashIdDTO>(DynamicAction dynamicAction, string? selfClaimId = null) where HashIdDTO : ShiftEntityDTOBase, new()
    {
        this.SelfClaimIdForTypeAuthValuesProvider = selfClaimId;
        this.DynamicAction = dynamicAction;
        this.TypeAuthDTOTypeForHashId = new HashIdDTO().GetType();
        return this;
    }

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
        if (this.KeySelector is not Expression<Func<TypeAuthValuesRepositoryGlobalFilterContext<T>, bool>> typedFilter)
            throw new InvalidOperationException("Invalid filter expression.");

        List<string>? claimValues = null; // Hardcoded example for the string

        var wildCardRead = false;
        var wildCardWrite = false;
        var wildCardDelete = false;
        var wildCardMaxAccess = false;

        List<string>? readableTypeAuthValues = null;
        List<string>? writableTypeAuthValues = null;
        List<string>? deletableTypeAuthValues = null;
        List<string>? maxAccessTypeAuthValues = null;

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

        var wildCardReadExpr = Expression.Constant(wildCardRead, typeof(bool));
        var wildCardWriteExpr = Expression.Constant(wildCardWrite, typeof(bool));
        var wildCardDeleteExpr = Expression.Constant(wildCardDelete, typeof(bool));
        var wildCardMaxAccessExpr = Expression.Constant(wildCardMaxAccess, typeof(bool));

        var readableTypeAuthValuesExpression = Expression.Constant(readableTypeAuthValues, typeof(List<string>));
        var writableTypeAuthValuesExpression = Expression.Constant(writableTypeAuthValues, typeof(List<string>));
        var deletableTypeAuthValuesExpression = Expression.Constant(deletableTypeAuthValues, typeof(List<string>));
        var maxAccessTypeAuthValuesExpression = Expression.Constant(maxAccessTypeAuthValues, typeof(List<string>));

        // Create the new visitor with all the necessary expressions.
        var visitor = new FilterExpressionVisitor<T>(
            typedFilter.Parameters[0],
            entityParam,

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

    public class FilterExpressionVisitor<T> : ExpressionVisitor
    {
        private readonly ParameterExpression _oldParameter;
        private readonly ParameterExpression _entityParameter;

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
                if (node.Member.Name == nameof(TypeAuthValuesRepositoryGlobalFilterContext<Entity>.Entity))
                    return _entityParameter;

                if (node.Member.Name == nameof(TypeAuthValuesRepositoryGlobalFilterContext<Entity>.WildCardRead))
                    return _wildcardReadExpression;

                if (node.Member.Name == nameof(TypeAuthValuesRepositoryGlobalFilterContext<Entity>.WildCardWrite))
                    return _wildcardWriteExpression;

                if (node.Member.Name == nameof(TypeAuthValuesRepositoryGlobalFilterContext<Entity>.WildCardDelete))
                    return _wildcardDeleteExpression;

                if (node.Member.Name == nameof(TypeAuthValuesRepositoryGlobalFilterContext<Entity>.WildCardMaxAccess))
                    return _wildcardMaxAccessExpression;



                if (node.Member.Name == nameof(TypeAuthValuesRepositoryGlobalFilterContext<Entity>.ReadableTypeAuthValues))
                    return _readableTypeAuthValuesExpression;

                if (node.Member.Name == nameof(TypeAuthValuesRepositoryGlobalFilterContext<Entity>.WritableTypeAuthValues))
                    return _writableTypeAuthValuesExpression;

                if (node.Member.Name == nameof(TypeAuthValuesRepositoryGlobalFilterContext<Entity>.DeletableTypeAuthValues))
                    return _deletableTypeAuthValuesExpression;

                if (node.Member.Name == nameof(TypeAuthValuesRepositoryGlobalFilterContext<Entity>.MaxAccessTypeAuthValues))
                    return _maxAccessTypeAuthValuesExpression;
            }

            // For all other members, continue visiting as normal.
            return base.VisitMember(node);
        }
    }
}