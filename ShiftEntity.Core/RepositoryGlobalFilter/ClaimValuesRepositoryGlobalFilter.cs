using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.Core.RepositoryGlobalFilter;

public class ClaimValuesRepositoryGlobalFilter<Entity> :
    IRepositoryGlobalFilter where Entity : ShiftEntity<Entity>
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

    public Expression<Func<T, bool>>? GetFilterExpression<T>() where T : ShiftEntity<T>
    {
        if (this.KeySelector is not Expression<Func<ClaimValuesRepositoryGlobalFilterContext<T>, bool>> filterContextExpression)
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

        var visitor = new FilterExpressionVisitor<T>(
            filterContextExpression.Parameters[0],
            entityParam,
            claimValuesExpression
        );

        var newBody = visitor.Visit(filterContextExpression.Body);

        return Expression.Lambda<Func<T, bool>>(newBody, entityParam);
    }

    public class FilterExpressionVisitor<T> : ExpressionVisitor
    {
        private readonly ParameterExpression _filterContextExpression;
        private readonly ParameterExpression _entityParameter;
        private readonly Expression _claimValuesExpression;

        public FilterExpressionVisitor(
            ParameterExpression filterContextExpression,
            ParameterExpression entityParameter,
            Expression claimValuesExpression
        )
        {
            _filterContextExpression = filterContextExpression;
            _entityParameter = entityParameter;
            _claimValuesExpression = claimValuesExpression;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == _filterContextExpression)
            {
                if (node.Member.Name == nameof(ClaimValuesRepositoryGlobalFilterContext<Entity>.Entity))
                    return _entityParameter;

                if (node.Member.Name == nameof(ClaimValuesRepositoryGlobalFilterContext<Entity>.ClaimValues))
                    return _claimValuesExpression;

                return base.VisitMember(node);
            }

            return base.VisitMember(node);
        }
    }
}