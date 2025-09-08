
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.TypeAuth.Core;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.EFCore;

public class ShiftRepositoryOptions<EntityType> where EntityType : ShiftEntity<EntityType>
{
    internal List<Action<IncludeOperations<EntityType>>> IncludeOperations { get; set; } = new();
    public Dictionary<Guid, IRepositoryGlobalFilter> GlobalFilters { get; set; } = new();
    public DefaultDataLevelAccessOptions DefaultDataLevelAccessOptions { get; set; } = new();
    internal ICurrentUserProvider? CurrentUserProvider { get; set; }
    internal ITypeAuthService? TypeAuthService { get; set; }

    public void IncludeRelatedEntitiesWithFindAsync(params Action<IncludeOperations<EntityType>>[] includeOperations)
    {
        this.IncludeOperations = includeOperations.ToList();
    }

    public CustomValueRepositoryGlobalFilter<EntityType, TValue> FilterByCustomValue<TValue>(
        Expression<Func<CustomValueRepositoryGlobalFilterContext<EntityType, TValue>, bool>> keySelector,
        Guid? id = null,
        bool disabled = false
    ) where TValue : class
    {
        var createdFilter = new CustomValueRepositoryGlobalFilter<EntityType, TValue>(keySelector)
        {
            ID = id ?? Guid.NewGuid(),
            Disabled = disabled
        };

        GlobalFilters.Add(createdFilter.ID, createdFilter);

        return createdFilter;
    }

    public ClaimValuesRepositoryGlobalFilter<EntityType> FilterByClaimValues(
        Expression<Func<ClaimValuesRepositoryGlobalFilterContext<EntityType>, bool>> keySelector, 
        Guid? id = null,
        bool disabled = false
    )
    {
        var createdFilter = new ClaimValuesRepositoryGlobalFilter<EntityType>(keySelector, this.CurrentUserProvider)
        {
            ID = id ?? Guid.NewGuid(),
            Disabled = disabled
        };

        GlobalFilters.Add(createdFilter.ID, createdFilter);

        return createdFilter;
    }

    public TypeAuthValuesRepositoryGlobalFilter<EntityType> FilterByTypeAuthValues(
        Expression<Func<TypeAuthValuesRepositoryGlobalFilterContext<EntityType>, bool>> keySelector, 
        Guid? id = null,
        bool disabled = false
    )
    {
        var createdFilter = new TypeAuthValuesRepositoryGlobalFilter<EntityType>(keySelector, this.CurrentUserProvider, this.TypeAuthService)
        {
            ID = id ?? Guid.NewGuid(),
            Disabled = disabled
        };

        GlobalFilters.Add(createdFilter.ID, createdFilter);

        return createdFilter;
    }
}