
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
    /// The mapper the repository uses. When the builder calls neither <see cref="UseMapper"/> nor
    /// <see cref="UseGeneratedMapper"/>, the repository resolves one itself, in this order:
    /// <list type="number">
    ///   <item>an <c>IShiftEntityMapper&lt;EntityType, ListDTO, ViewAndUpsertDTO&gt;</c> registered in DI;</item>
    ///   <item>the SOURCE-GENERATED mapper for the triple, from <see cref="ShiftEntityMapperRegistry"/>.</item>
    /// </list>
    /// There is no further fallback: when neither covers the triple this stays <see langword="null"/> and the
    /// repository's mapping methods throw unless it overrides them. That case is caught at startup by
    /// <see cref="ShiftEntityMapperValidation"/>, not on the first request.
    /// </summary>
    public IShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>? Mapper { get; internal set; }

    /// <summary>
    /// True once <see cref="UseMapper"/> or <see cref="UseGeneratedMapper"/> has been called — tells the
    /// repository not to overwrite the programmer's choice (including an explicit <see langword="null"/>)
    /// with a mapper resolved from DI or from <see cref="ShiftEntityMapperRegistry"/>.
    /// </summary>
    internal bool MapperConfigured { get; private set; }

    /// <summary>
    /// Sets the mapper the repository uses, ahead of anything the repository would resolve on its own (a
    /// DI registration, then the source-generated mapper). Pass <see langword="null"/> to use no mapper at
    /// all — the repository must then override the mapping methods, which otherwise throw.
    /// </summary>
    public void UseMapper(IShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>? mapper)
    {
        this.Mapper = mapper;
        this.MapperConfigured = true;
    }

    /// <summary>
    /// Uses the SOURCE-GENERATED mapper for this repository's (entity, list, view) triple, explicitly and
    /// ahead of any <c>IShiftEntityMapper</c> registered in DI for the same triple. Mappers are generated
    /// automatically for every triple the source generator discovers (repository declarations and endpoint
    /// attributes) and registered in <see cref="ShiftEntityMapperRegistry"/> at module load — no mapper class
    /// is declared by hand.
    /// </summary>
    /// <param name="configure">
    /// Optional per-property customization (<c>ForView</c>/<c>ForList</c>/<c>ForEntity</c>/<c>ForCopy</c>).
    /// Applied after the mapper's own <c>Configure</c> partial hook, so registrations here win over the
    /// shared mapper configuration. Registering a member automatically suppresses the generated
    /// convention for it. For triple-wide customization, declare a <c>[ShiftEntityMapper]</c> partial
    /// class and implement <c>Configure</c> there instead.
    /// </param>
    public void UseGeneratedMapper(Action<ShiftMapperBuilder<EntityType, ListDTO, ViewAndUpsertDTO>>? configure = null)
    {
        var mapperType = ShiftEntityMapperRegistry.Find(typeof(EntityType), typeof(ListDTO), typeof(ViewAndUpsertDTO))
            ?? throw new InvalidOperationException(
                $"No source-generated mapper is registered for ({typeof(EntityType).Name}, {typeof(ListDTO).Name}, {typeof(ViewAndUpsertDTO).Name}). " +
                "Ensure the ShiftEntity source generator runs on the assembly declaring the repository (triples are discovered automatically), " +
                "or declare a [ShiftEntityMapper] partial class for this exact triple.");

        var mapper = (IShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>)Activator.CreateInstance(mapperType)!;

        if (configure is not null)
        {
            if (mapper is not IShiftMapperConfigurable<EntityType, ListDTO, ViewAndUpsertDTO> configurable)
                throw new InvalidOperationException(
                    $"The source-generated mapper '{mapperType.Name}' does not support per-property configuration — " +
                    "rebuild so the generator emits the configuration hook.");

            configurable.AddConfiguration(configure);
        }

        this.Mapper = mapper;
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