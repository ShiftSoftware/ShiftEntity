
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
    /// The compiled explicit policy declared via <see cref="DataLevelAccess"/>, or <see langword="null"/> when none
    /// was declared. A repository can still have an effective automatic policy supplied by its host profile; use
    /// <c>ShiftRepository.DataLevelAccessPolicy</c> when applying the effective policy manually.
    /// </summary>
    public DataLevelAccessPolicy<EntityType>? DataLevelAccessPolicy { get; private set; }

    /// <summary>
    /// The validated declaration retained so a host profile can compose its automatic dimensions with explicit
    /// repository overrides when the effective policy is resolved lazily.
    /// </summary>
    internal DataLevelAccessBuilder<EntityType>? DataLevelAccessDeclaration { get; private set; }
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
    /// The mapper the repository uses. When the builder does not call <see cref="UseMapper"/>, the repository
    /// resolves one itself, in this order:
    /// <list type="number">
    ///   <item>an <c>IShiftEntityMapper&lt;EntityType, ListDTO, ViewAndUpsertDTO&gt;</c> registered in DI;</item>
    ///   <item>the host's ShiftMapper, whose generated mapper declares this triple's four maps automatically
    ///   (replaced per pair by a <c>CreateMap</c> in a mapper class; nested as deep as <see cref="Mapping"/>
    ///   says) — the default.</item>
    /// </list>
    /// There is no further fallback: when neither covers the triple this stays <see langword="null"/> and the
    /// repository's mapping methods throw unless it overrides them. That case is caught at startup by
    /// <see cref="ShiftEntityMapperValidation"/>, not on the first request.
    /// </summary>
    public IShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>? Mapper { get; internal set; }

    /// <summary>
    /// True once <see cref="UseMapper"/> has been called — tells the repository not to overwrite the programmer's
    /// choice (including an explicit <see langword="null"/>) with a mapper resolved from DI or from ShiftMapper.
    /// </summary>
    internal bool MapperConfigured { get; private set; }

    /// <summary>
    /// Sets the mapper the repository uses, ahead of anything the repository would resolve on its own (a
    /// DI registration, then the host's ShiftMapper). Pass <see langword="null"/> to use no mapper at
    /// all — the repository must then override the mapping methods, which otherwise throw.
    /// </summary>
    public void UseMapper(IShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>? mapper)
    {
        this.Mapper = mapper;
        this.MapperConfigured = true;
    }

    /// <summary>
    /// The depth <see cref="Mapping"/> asked for with <c>m.Nested(n)</c>, or <see langword="null"/> when the
    /// repository left the framework's default (10). Recorded for inspection only: the depth itself is read at
    /// build time and baked into the maps.
    /// </summary>
    public int? NestedMappingDepth { get; private set; }

    /// <summary>
    /// Says how far the repository's AUTOMATIC maps reach. The four ShiftMapper declares for this triple from
    /// the repository's type arguments (entity ↔ view, entity → list, entity → entity) nest the class-typed
    /// members below them, ten levels deep by default; this caps it:
    /// <code>
    /// o.Mapping(m =&gt; m.Nested(2));
    /// </code>
    /// The lambda is read at BUILD time by the ShiftMapper generator and the depth baked into the maps, so it is
    /// a constant and the call a plain statement of the lambda (SM0035 otherwise).
    /// <para>It is the only thing a repository says about its maps: WHAT a member maps from is not the
    /// repository's concern. To customize a member, write an ordinary <c>CreateMap</c> for the pair in a
    /// <c>ShiftMapperBase</c> class — it replaces the automatic map for that pair (SM0047, informational), the
    /// other pairs of the triple stay automatic, and the customized map is the one every service maps the pair
    /// through as well.</para>
    /// </summary>
    public void Mapping(Action<ShiftEntityMapping<EntityType, ListDTO, ViewAndUpsertDTO>> configure)
    {
        if (configure is null)
            throw new ArgumentNullException(nameof(configure));

        var surface = new ShiftEntityMapping<EntityType, ListDTO, ViewAndUpsertDTO>();
        configure(surface);

        if (surface.Depth is { } depth)
            this.NestedMappingDepth = depth;
    }

    /// <summary>
    /// Declares the entity's v2 data-level access dimensions (see <see cref="DataLevelAccessBuilder{TEntity}"/>:
    /// <c>On(action).Key/Keys/Match</c>, <c>OnOwner(claim)</c>, <c>Unscoped()</c>; dimensions AND-compose, a
    /// dimension's key columns are OR-internal) and compiles them into <see cref="DataLevelAccessPolicy"/>.
    /// Compilation validates fail-closed — a dimension declared without a predicate throws here, at startup,
    /// not at query time.
    /// </summary>
    /// <remarks>
    /// The repository enforces the compiled policy on query and per-operation row paths. When the host opted into a
    /// default profile, this declaration overlays it: TypeAuth actions declared here replace matching automatic
    /// dimensions and unrelated defaults remain. Declaring twice throws; silently overwriting a security declaration
    /// would be a leak waiting to happen.
    /// </remarks>
    public void DataLevelAccess(Action<DataLevelAccessBuilder<EntityType>> declare)
    {
        if (declare is null)
            throw new ArgumentNullException(nameof(declare));
        if (this.DataLevelAccessPolicy is not null)
            throw new InvalidOperationException($"Data-level access has already been declared for {typeof(EntityType).Name}.");

        var builder = new DataLevelAccessBuilder<EntityType>();
        declare(builder);

        var policy = new DataLevelAccessPolicy<EntityType>(builder);

        this.DataLevelAccessDeclaration = builder;
        this.DataLevelAccessPolicy = policy;
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
