
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
    /// The mapper the repository uses. When the builder calls neither <see cref="UseMapper"/> nor
    /// <see cref="UseGeneratedMapper"/>, the repository resolves one itself, in this order:
    /// <list type="number">
    ///   <item>an <c>IShiftEntityMapper&lt;EntityType, ListDTO, ViewAndUpsertDTO&gt;</c> registered in DI;</item>
    ///   <item>the host's ShiftMapper, whose generated mapper declares this triple's four maps automatically
    ///   (customized with <see cref="Mapping"/>) — the default;</item>
    ///   <item>for one release, the OLD source-generated mapper from <see cref="ShiftEntityMapperRegistry"/>.</item>
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
    /// <b>Obsolete — the OLD generated mapping.</b> Uses the mapper <c>ShiftEntity.SourceGenerator</c> wrote for
    /// this repository's (entity, list, view) triple, from <see cref="ShiftEntityMapperRegistry"/>, ahead of the
    /// ShiftMapper maps the repository resolves by default. Kept for one release so a project migrates at its
    /// own pace: delete the call (the automatic maps are ShiftMapper's now) and move what the lambda configured
    /// into <see cref="Mapping"/> — the migration guide in ShiftTemplates
    /// (<c>docs/plans/repository-mapping-on-shiftmapper/04-migration-guide.md</c>) has the line-for-line table.
    /// </summary>
    /// <param name="configure">
    /// Optional per-property customization (<c>ForView</c>/<c>ForList</c>/<c>ForEntity</c>/<c>ForCopy</c>).
    /// Applied after the mapper's own <c>Configure</c> partial hook, so registrations here win over the
    /// shared mapper configuration. Registering a member automatically suppresses the generated
    /// convention for it. For triple-wide customization, declare a <c>[ShiftEntityMapper]</c> partial
    /// class and implement <c>Configure</c> there instead.
    /// </param>
    [Obsolete("The repository's maps are declared by ShiftMapper now. Delete this call; move the lambda's lines into Mapping(m => ...) — see docs/plans/repository-mapping-on-shiftmapper/04-migration-guide.md in ShiftTemplates. Removed in the next release.")]
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
    /// What <see cref="Mapping"/> was given, run by the repository into a <see cref="ShiftEntityMapping{EntityType, ListDTO, ViewAndUpsertDTO}"/>
    /// and handed to the host's <c>IMapper</c> when the repository is constructed. Null when nothing was configured.
    /// </summary>
    internal Action<ShiftEntityMapping<EntityType, ListDTO, ViewAndUpsertDTO>>? MappingConfiguration { get; private set; }

    /// <summary>
    /// Customizes the repository's AUTOMATIC maps — the four ShiftMapper declares for this triple from the
    /// repository's type arguments (entity ↔ view, entity → list, entity → entity) — member by member, in the
    /// ShiftMapper vocabulary:
    /// <code>
    /// o.Mapping(m =&gt; m.List.ForMember(d =&gt; d.Total, opt =&gt; opt.MapFrom(i =&gt; i.Lines.Sum(l =&gt; l.Amount))));
    /// o.Mapping(m =&gt; m.View.ForMember(d =&gt; d.Secret, opt =&gt; opt.Ignore()));
    /// </code>
    /// The lambda is read at BUILD time by the ShiftMapper generator for its shape — which members are customized
    /// — and baked into the maps; its value delegates stay at run time and reach the mapper through this
    /// repository. Write it as plain statements: a call behind an <c>if</c> or a loop is refused by the build
    /// (SM0035), and the condition belongs inside the value. Maps customized here are still ordinary maps any
    /// service can run through <c>Mapper</c>; a <c>CreateMap</c> for the same pair in a mapper class replaces the
    /// automatic map, this configuration included (SM0047, then SM0052 if the configuration is left behind).
    /// Two repositories configuring one pair is a build error (SM0050).
    /// </summary>
    public void Mapping(Action<ShiftEntityMapping<EntityType, ListDTO, ViewAndUpsertDTO>> configure)
    {
        if (configure is null)
            throw new ArgumentNullException(nameof(configure));

        this.MappingConfiguration = this.MappingConfiguration is { } existing
            ? surface => { existing(surface); configure(surface); }
            : configure;
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
