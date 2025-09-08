using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.Core.RepositoryGlobalFilter;

public class ClaimValuesRepositoryGlobalFilter<Entity> : IRepositoryGlobalFilter
    where Entity : ShiftEntity<Entity>
{
    public Guid ID { get; set; }
    public bool Disabled { get; set; }
    public Expression<Func<ClaimValuesRepositoryGlobalFilterContext<Entity>, bool>> KeySelector { get; set; }
    public Type? DTOTypeForHashId { get; set; }
    public string? ClaimIdForClaimValuesProvider { get; set; }

    private ICurrentUserProvider? CurrentUserProvider;

    public ClaimValuesRepositoryGlobalFilter(
        Expression<Func<ClaimValuesRepositoryGlobalFilterContext<Entity>, bool>> keySelector,
        ICurrentUserProvider? currentUserProvider
    )
    {
        this.KeySelector = keySelector;
        this.CurrentUserProvider = currentUserProvider;
    }

    public ClaimValuesRepositoryGlobalFilter<Entity> ClaimValuesProvider(string claimId)
    {
        this.ClaimIdForClaimValuesProvider = claimId;
        return this;
    }

    public ClaimValuesRepositoryGlobalFilter<Entity> ClaimValuesProvider<HashIdDTO>(string claimId) where HashIdDTO : ShiftEntityDTOBase, new()
    {
        this.ClaimIdForClaimValuesProvider = claimId;
        this.DTOTypeForHashId = new HashIdDTO().GetType();
        return this;
    }

    Expression<Func<T, bool>>? IRepositoryGlobalFilter.GetFilterExpression<T>()
    {
        if (this.KeySelector is not Expression<Func<ClaimValuesRepositoryGlobalFilterContext<T>, bool>> typedFilter)
            throw new InvalidOperationException("Invalid filter expression.");

        List<string>? claimValues = null;

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

        var entityParam = Expression.Parameter(typeof(T), "entity");
        var claimValuesExpression = Expression.Constant(claimValues, typeof(List<string>));


        // Create the new visitor with all the necessary expressions.
        var visitor = new FilterExpressionVisitor<T>(
            typedFilter.Parameters[0],
            entityParam,
            claimValuesExpression
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
        private readonly Expression _claimValuesExpression;

        public FilterExpressionVisitor(
            ParameterExpression oldParameter,
            ParameterExpression entityParameter,
            Expression claimValuesExpression
        )
        {
            _oldParameter = oldParameter;
            _entityParameter = entityParameter;
            _claimValuesExpression = claimValuesExpression;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            // Check if the member access is on the old parameter
            if (node.Expression == _oldParameter)
            {
                if (node.Member.Name == nameof(ClaimValuesRepositoryGlobalFilterContext<Entity>.Entity))
                    return _entityParameter;


                if (node.Member.Name == nameof(ClaimValuesRepositoryGlobalFilterContext<Entity>.ClaimValues))
                    return _claimValuesExpression;

                // For all other members, continue visiting as normal.
                return base.VisitMember(node);
            }

            return base.VisitMember(node);
        }
    }
}