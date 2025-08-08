
using ShiftSoftware.ShiftEntity.Core;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.EFCore;

public class ShiftRepositoryOptions<EntityType> where EntityType : ShiftEntity<EntityType>
{
    internal List<Action<IncludeOperations<EntityType>>> IncludeOperations { get; set; } = new();
    internal List<IRepositoryGlobalFilter> GlobalFilters { get; set; } = new();
    public void IncludeRelatedEntitiesWithFindAsync(params Action<IncludeOperations<EntityType>>[] includeOperations)
    {
        this.IncludeOperations = includeOperations.ToList();
    }

    public RepositoryGlobalFilter<EntityType, TValue> FilterBy<TValue>(Expression<Func<RepositoryGlobalFilterContext<EntityType, TValue>, bool>> keySelector)
    {
        var createdFilter = new RepositoryGlobalFilter<EntityType, TValue>(keySelector);

        GlobalFilters.Add(createdFilter);

        return createdFilter;
    }

}