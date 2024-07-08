
using ShiftSoftware.TypeAuth.Core.Actions;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.Web;

public class DynamicActionFilterBy<Entity>
{
    public DynamicAction? DynamicAction { get; set; }
    public List<string>? AccessibleKeys { get; set; }
    public string? ClaimId { get; set; }
    public ParameterExpression ParameterExpression { get; set; }
    public InvocationExpression InvocationExpression { get; set; }
    public Type TKey { get; set; }
    public Expression<Func<Entity, long?>>? CreatedByUserIDKeySelector { get; set; }
    public Type? DTOTypeForHashId { get; set; }
    public bool ShowNulls { get; set; }
    public string? SelfClaimId { get; set; }

    public DynamicActionFilterBy(DynamicAction dynamicAction, InvocationExpression invocationExpression, ParameterExpression parameterExpression, Type tKey)
    {
        this.DynamicAction = dynamicAction;
        this.InvocationExpression = invocationExpression;
        this.ParameterExpression = parameterExpression;
        this.TKey = tKey;
    }

    public DynamicActionFilterBy(List<string> accessibleKeys, InvocationExpression invocationExpression, ParameterExpression parameterExpression, Type tKey)
    {
        this.AccessibleKeys = accessibleKeys;
        this.InvocationExpression = invocationExpression;
        this.ParameterExpression = parameterExpression;
        this.TKey = tKey;
    }

    public DynamicActionFilterBy(string claimId, InvocationExpression invocationExpression, ParameterExpression parameterExpression, Type tKey)
    {
        this.ClaimId = claimId;
        this.InvocationExpression = invocationExpression;
        this.ParameterExpression = parameterExpression;
        this.TKey = tKey;
    }

    public DynamicActionFilterBy<Entity> IncludeNulls()
    {
        this.ShowNulls = true;

        return this;
    }

    public DynamicActionFilterBy<Entity> IncludeCreatedByCurrentUser(Expression<Func<Entity, long?>>? keySelector)
    {
        this.CreatedByUserIDKeySelector = keySelector;

        return this;
    }

    public DynamicActionFilterBy<Entity> IncludeSelfItems(string selfClaimId)
    {
        this.SelfClaimId = selfClaimId;

        return this;
    }

    public DynamicActionFilterBy<Entity> DecodeHashId<DTO>()
    {
        this.DTOTypeForHashId = typeof(DTO);

        return this;
    }
}
