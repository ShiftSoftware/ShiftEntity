
using ShiftSoftware.TypeAuth.Core.Actions;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.Web;

public class DynamicActionFilterBuilder<Entity>
{
    internal List<DynamicActionFilterBy<Entity>> DynamicActionFilters { get; set; } = new List<DynamicActionFilterBy<Entity>>();

    //public Func<IDynamicActionExpressionBuilder, Expression<Func<Entity, bool>>>? DynamicActionExpressionBuilder { get; set; }

    public bool DisableDefaultRegionFilter { get; set; }
    public bool DisableDefaultCompanyFilter { get; set; }
    public bool DisableDefaultCompanyBranchFilter { get; set; }
    public bool DisableDefaultTeamFilter { get; set; }

    public DynamicActionFilterBy<Entity> FilterBy<TKey>(Expression<Func<Entity, TKey>> keySelector, DynamicAction dynamicAction)
    {
        var parameter = Expression.Parameter(typeof(Entity));

        // Build expression for ids.Contains(x.ID)
        var keySelectorInvoke = Expression.Invoke(keySelector, parameter);

        var createdFilter = new DynamicActionFilterBy<Entity>(dynamicAction, keySelectorInvoke, parameter, typeof(TKey));

        DynamicActionFilters.Add(createdFilter);

        return createdFilter;
    }

    public DynamicActionFilterBy<Entity> FilterBy<TKey>(Expression<Func<Entity, TKey>> keySelector, List<string> accessibleKeys)
    {
        var parameter = Expression.Parameter(typeof(Entity));

        // Build expression for ids.Contains(x.ID)
        var keySelectorInvoke = Expression.Invoke(keySelector, parameter);

        var createdFilter = new DynamicActionFilterBy<Entity>(accessibleKeys, keySelectorInvoke, parameter, typeof(TKey));

        DynamicActionFilters.Add(createdFilter);

        return createdFilter;
    }

    public DynamicActionFilterBy<Entity> FilterBy<TKey>(Expression<Func<Entity, TKey>> keySelector, string claimId)
    {
        var parameter = Expression.Parameter(typeof(Entity));

        // Build expression for ids.Contains(x.ID)
        var keySelectorInvoke = Expression.Invoke(keySelector, parameter);

        var createdFilter = new DynamicActionFilterBy<Entity>(claimId, keySelectorInvoke, parameter, typeof(TKey));

        DynamicActionFilters.Add(createdFilter);

        return createdFilter;
    }
}