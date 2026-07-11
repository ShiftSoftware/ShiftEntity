
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.Core.GlobalRepositoryFilter;
using ShiftSoftware.TypeAuth.Core;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.EFCore;

public class ShiftRepositoryOptions<EntityType, ListDTO, ViewAndUpsertDTO> where EntityType : ShiftEntity<EntityType>
{
    internal List<Action<IncludeOperations<EntityType>>> IncludeOperations { get; set; } = new();
    public Dictionary<Guid, IGlobalRepositoryFilter> GlobalRepositoryFilters { get; set; } = new();
    public DefaultDataLevelAccessOptions DefaultDataLevelAccessOptions { get; set; } = new();

    /// <summary>
    /// The compiled v2 data-level policy declared via <see cref="DataLevelAccess"/>, or <see langword="null"/> when
    /// none was declared (the repository then keeps today's legacy behavior). Recorded here in Phase 2.5; consumed by
    /// <c>ShiftRepository</c>'s query/row paths in Phase 3.
    /// </summary>
    public DataLevelAccessPolicy<EntityType>? DataLevelAccessPolicy { get; private set; }
    private ICurrentUserProvider? CurrentUserProvider { get; set; }
    private ITypeAuthService? TypeAuthService { get; set; }
    private IHashIdService? HashIdService { get; set; }

    public void SetCurrentUserProvider(ICurrentUserProvider currentUserProvider)
    {
        this.CurrentUserProvider = currentUserProvider;
    }

    public void SetTypeAuthService(ITypeAuthService typeAuthService)
    {
        this.TypeAuthService = typeAuthService;
    }

    public void SetHashIdService(IHashIdService hashIdService)
    {
        this.HashIdService = hashIdService;
    }

    public void IncludeRelatedEntitiesWithFindAsync(params Action<IncludeOperations<EntityType>>[] includeOperations)
    {
        this.IncludeOperations = includeOperations.ToList();
    }

    /// <summary>
    /// The mapper the repository uses. When the builder doesn't call <see cref="UseMapper"/>, the
    /// repository fills this with the default AutoMapper-backed mapper (when AutoMapper is registered).
    /// </summary>
    public IShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>? Mapper { get; internal set; }

    /// <summary>
    /// True once <see cref="UseMapper"/> has been called — tells the repository not to overwrite the
    /// programmer's choice (including an explicit <see langword="null"/>) with the AutoMapper default.
    /// </summary>
    internal bool MapperConfigured { get; private set; }

    /// <summary>
    /// Sets the mapper the repository uses, overriding the AutoMapper default (even when AutoMapper is
    /// registered). Pass <see langword="null"/> to use no mapper (override the mapping methods instead,
    /// or — in future — rely on source-generated mapping).
    /// </summary>
    public void UseMapper(IShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>? mapper)
    {
        this.Mapper = mapper;
        this.MapperConfigured = true;
    }

    /// <summary>
    /// Uses the SOURCE-GENERATED mapper for this repository's (entity, list, view) triple, overriding
    /// the AutoMapper default. Mappers are generated automatically for every triple the source generator
    /// discovers (repository declarations and endpoint attributes) and registered in
    /// <see cref="ShiftEntityMapperRegistry"/> at module load — no mapper class is declared by hand.
    /// To customize the mapping, declare a <c>[ShiftEntityMapper]</c> partial class for the triple.
    /// </summary>
    public void UseGeneratedMapper()
    {
        var mapperType = ShiftEntityMapperRegistry.Find(typeof(EntityType), typeof(ListDTO), typeof(ViewAndUpsertDTO))
            ?? throw new InvalidOperationException(
                $"No source-generated mapper is registered for ({typeof(EntityType).Name}, {typeof(ListDTO).Name}, {typeof(ViewAndUpsertDTO).Name}). " +
                "Ensure the ShiftEntity source generator runs on the assembly declaring the repository (triples are discovered automatically), " +
                "or declare a [ShiftEntityMapper] partial class for this exact triple.");

        this.Mapper = (IShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>)Activator.CreateInstance(mapperType)!;
        this.MapperConfigured = true;
    }

    /// <summary>
    /// Declares the entity's v2 data-level access dimensions (see <see cref="DataLevelAccessBuilder{TEntity}"/>:
    /// <c>On(action).Key/Keys/Match</c>, <c>OnOwner(claim)</c>, <c>Unscoped()</c>; dimensions AND-compose, a
    /// dimension's key columns are OR-internal) and compiles them into <see cref="DataLevelAccessPolicy"/>.
    /// Compilation validates fail-closed — a dimension declared without a predicate throws here, at startup,
    /// not at query time.
    /// </summary>
    /// <remarks>
    /// Phase 2.5: the policy is recorded on the options only — <c>ShiftRepository</c> starts enforcing it
    /// (query filter + per-operation row authorization) in Phase 3. Declaring twice throws: one entity has one
    /// policy, and a silent overwrite of a security declaration would be a leak waiting to happen.
    /// </remarks>
    public void DataLevelAccess(Action<DataLevelAccessBuilder<EntityType>> declare)
    {
        if (declare is null)
            throw new ArgumentNullException(nameof(declare));
        if (this.DataLevelAccessPolicy is not null)
            throw new InvalidOperationException($"Data-level access has already been declared for {typeof(EntityType).Name}.");

        var builder = new DataLevelAccessBuilder<EntityType>();
        declare(builder);

        this.DataLevelAccessPolicy = new DataLevelAccessPolicy<EntityType>(builder);
    }

    public CustomValueFilter<EntityType, TValue> FilterByCustomValue<TValue>(
        Expression<Func<CustomValueFilterContext<EntityType, TValue>, bool>> keySelector,
        Guid? id = null,
        bool disabled = false
    ) where TValue : class
    {
        var createdFilter = new CustomValueFilter<EntityType, TValue>(keySelector, id ?? Guid.NewGuid())
        {
            Disabled = disabled
        };

        GlobalRepositoryFilters.Add(createdFilter.ID, createdFilter);

        return createdFilter;
    }

    public ClaimValuesFilter<EntityType> FilterByClaimValues(
        Expression<Func<ClaimValuesFilterContext<EntityType>, bool>> keySelector, 
        Guid? id = null,
        bool disabled = false
    )
    {
        var createdFilter = new ClaimValuesFilter<EntityType>(
            keySelector,
            this.CurrentUserProvider,
            this.HashIdService,
            id ?? Guid.NewGuid()
        )
        {
            Disabled = disabled
        };

        GlobalRepositoryFilters.Add(createdFilter.ID, createdFilter);

        return createdFilter;
    }

    public TypeAuthValuesFilter<EntityType> FilterByTypeAuthValues(
        Expression<Func<TypeAuthValuesFilterContext<EntityType>, bool>> keySelector, 
        Guid? id = null,
        bool disabled = false
    )
    {
        var createdFilter = new TypeAuthValuesFilter<EntityType>(
            keySelector,
            this.CurrentUserProvider,
            this.TypeAuthService,
            this.HashIdService,
            id ?? Guid.NewGuid()
        )
        {
            Disabled = disabled
        };

        GlobalRepositoryFilters.Add(createdFilter.ID, createdFilter);

        return createdFilter;
    }
}