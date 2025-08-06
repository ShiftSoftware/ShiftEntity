
using ShiftSoftware.ShiftEntity.Core;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.Web;

public class FilterContext<TEntity, TValue> where TEntity : ShiftEntity<TEntity>
{
    public TEntity Entity { get; }
    public TValue Value { get; }
    public bool WildCard { get; set; }

    public FilterContext(TEntity entity, TValue value)
    {
        Entity = entity;
        Value = value;
    }
}

public class DynamicActionFilterBuilder<Entity> where Entity : ShiftEntity<Entity>
{
    public bool DisableDefaultCountryFilter { get; set; }
    public bool DisableDefaultRegionFilter { get; set; }
    public bool DisableDefaultCompanyFilter { get; set; }
    public bool DisableDefaultCompanyBranchFilter { get; set; }
    public bool DisableDefaultTeamFilter { get; set; }
    public bool DisableDefaultBrandFilter { get; set; }
    public bool DisableDefaultCityFilter { get; set; }


    //internal List<DynamicActionFilter<Entity, List<long>>> DynamicActionFilters { get; set; } = new List<DynamicActionFilter<Entity, List<long>>>();
    internal List<IDynamicActionFilter> DynamicActionFilters { get; set; } = new();

    public DynamicActionFilter<Entity, TValue> FilterBy<TValue>(Expression<Func<FilterContext<Entity, TValue>, bool>> keySelector)
    {
        var createdFilter = new DynamicActionFilter<Entity, TValue>(keySelector);

        DynamicActionFilters.Add(createdFilter);

        return createdFilter;
    }

    //public DynamicActionFilter<Entity> FilterBy<TKey>(Expression<Func<Entity, TKey>> keySelector, List<string> accessibleKeys)
    //{
    //    var parameter = Expression.Parameter(typeof(Entity));

    //    // Build expression for ids.Contains(x.ID)
    //    var keySelectorInvoke = Expression.Invoke(keySelector, parameter);

    //    var createdFilter = new DynamicActionFilter<Entity>(accessibleKeys, keySelectorInvoke, parameter, typeof(TKey));

    //    DynamicActionFilters.Add(createdFilter);

    //    return createdFilter;
    //}

    //public DynamicActionFilter<Entity> FilterBy<TKey>(Expression<Func<Entity, TKey>> keySelector, string claimId)
    //{
    //    var parameter = Expression.Parameter(typeof(Entity));

    //    // Build expression for ids.Contains(x.ID)
    //    var keySelectorInvoke = Expression.Invoke(keySelector, parameter);

    //    var createdFilter = new DynamicActionFilter<Entity>(claimId, keySelectorInvoke, parameter, typeof(TKey));

    //    DynamicActionFilters.Add(createdFilter);

    //    return createdFilter;
    //}
}