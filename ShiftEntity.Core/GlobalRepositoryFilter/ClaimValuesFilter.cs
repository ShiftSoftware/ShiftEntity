using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.GlobalRepositoryFilter;

public class ClaimValuesFilter<Entity> :
    IGlobalRepositoryFilter where Entity : ShiftEntity<Entity>
{
    public Guid ID { get; }
    public bool Disabled { get; set; }
    private Expression<Func<ClaimValuesFilterContext<Entity>, bool>> KeySelector { get; }
    private Type? DTOTypeForHashId { get; set; }
    private string? ClaimIdForClaimValuesProvider { get; set; }

    private readonly ICurrentUserProvider? CurrentUserProvider;

    public ClaimValuesFilter(
        Expression<Func<ClaimValuesFilterContext<Entity>, bool>> keySelector,
        ICurrentUserProvider? currentUserProvider,
        Guid id
    )
    {
        this.ID = id;
        this.KeySelector = keySelector;
        this.CurrentUserProvider = currentUserProvider;
    }

    public ClaimValuesFilter<Entity> ValueProvider(string claimId)
    {
        this.ClaimIdForClaimValuesProvider = claimId;
        return this;
    }

    public ClaimValuesFilter<Entity> ValueProvider<HashIdDTO>(string claimId) where HashIdDTO : ShiftEntityDTOBase, new()
    {
        this.ClaimIdForClaimValuesProvider = claimId;
        this.DTOTypeForHashId = new HashIdDTO().GetType();
        return this;
    }

    public ValueTask<Expression<Func<T, bool>>?> GetFilterExpression<T>() where T : ShiftEntity<T>
    {
        if (this.KeySelector is not Expression<Func<ClaimValuesFilterContext<T>, bool>> filterContextExpression)
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

        return new ValueTask<Expression<Func<T, bool>>?>(Expression.Lambda<Func<T, bool>>(newBody, entityParam));
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
                if (node.Member.Name == nameof(ClaimValuesFilterContext<Entity>.Entity))
                    return _entityParameter;

                if (node.Member.Name == nameof(ClaimValuesFilterContext<Entity>.ClaimValues))
                    return _claimValuesExpression;

                return base.VisitMember(node);
            }

            return base.VisitMember(node);
        }
    }
}