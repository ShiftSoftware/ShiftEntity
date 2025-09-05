
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.EFCore;

public class ShiftRepositoryOptions<EntityType> where EntityType : ShiftEntity<EntityType>
{
    /// <summary>
    /// Applies the default data level access on repository level instead of ShiftEntitySecureController level
    /// </summary>
    public bool UseDefaultDataLevelAccess { get; set; } = true;
    internal List<Action<IncludeOperations<EntityType>>> IncludeOperations { get; set; } = new();
    internal List<IRepositoryGlobalFilter> GlobalFilters { get; set; } = new();
    public DefaultDataLevelAccessOptions DefaultDataLevelAccessOptions { get; set; } = new();
    
    internal ICurrentUserProvider? CurrentUserProvider { get; set; }
    internal ITypeAuthService? TypeAuthService { get; set; }
    
    public void IncludeRelatedEntitiesWithFindAsync(params Action<IncludeOperations<EntityType>>[] includeOperations)
    {
        this.IncludeOperations = includeOperations.ToList();
    }

    public RepositoryGlobalFilter<EntityType, TValue> FilterBy<TValue>(Expression<Func<RepositoryGlobalFilterContext<EntityType, TValue>, bool>> keySelector) 
        where TValue : class
    {
        var createdFilter = new RepositoryGlobalFilter<EntityType, TValue>(
            keySelector, 
            CurrentUserProvider, 
            TypeAuthService
        );

        GlobalFilters.Add(createdFilter);

        return createdFilter;
    }

    public TypeAuthGlobalFilter TypeAuth(DynamicAction dynamicAction)
    {
        var createdFilter = new TypeAuthGlobalFilter(dynamicAction);

        GlobalFilters.Add(createdFilter);

        return createdFilter;
    }

    public TypeAuthGlobalFilter TypeAuth(DynamicAction dynamicAction, string claimId)
    {
        var createdFilter = new TypeAuthGlobalFilter(dynamicAction, claimId);

        GlobalFilters.Add(createdFilter);

        return createdFilter;
    }

    public TypeAuthGlobalFilter TypeAuth<HashIdDTO>(DynamicAction dynamicAction) where HashIdDTO : ShiftEntityDTOBase, new()
    {
        var createdFilter = new TypeAuthGlobalFilter(dynamicAction, new HashIdDTO().GetType());

        GlobalFilters.Add(createdFilter);

        return createdFilter;
    }

    public TypeAuthGlobalFilter TypeAuth<HashIdDTO>(DynamicAction dynamicAction, string claimId) where HashIdDTO : ShiftEntityDTOBase, new()
    {
        var createdFilter = new TypeAuthGlobalFilter(dynamicAction, new HashIdDTO().GetType(), claimId);

        GlobalFilters.Add(createdFilter);

        return createdFilter;
    }
}