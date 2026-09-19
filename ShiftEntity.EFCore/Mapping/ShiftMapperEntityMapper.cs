using ShiftMapper;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Mapping;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System.Net;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// The repository's door onto ShiftMapper: an <see cref="IShiftEntityMapper{TEntity, TListDTO, TViewDTO}"/> whose
/// four methods are the four maps the repository's marker declared — entity ↔ view, entity → list, entity → entity
/// — run through the host's registered <see cref="IMapper"/>.
///
/// <para>Resolved by <c>ShiftRepository.InitCommon</c> when the mapper <see cref="Covers"/> the triple. Public so a
/// test can measure the same door the repository uses — the sample's parity harness diffs it against the goldens
/// — but nothing else needs to construct it: the maps it runs are ordinary maps any service can run through
/// <c>Mapper</c> directly.</para>
///
/// <para>Two things happen around a write. The action (<c>Insert</c>/<c>Update</c>) is published on the scoped
/// <see cref="IShiftEntityMappingContext"/> for the duration of the map, since a ShiftMapper map has no context
/// parameter of its own. And a value ShiftMapper could not convert — text that is not a number, a date that is
/// not a date — comes out as a 400 naming the request field (<see cref="ShiftEntityException"/>, the same
/// <c>Model Validation Error</c> shape <c>MappingHelpers.ToForeignKey</c> produces), because it is the client's
/// mistake and not a server fault. A blank required select is already that shape when it leaves the rules pack.</para>
/// </summary>
public sealed class ShiftMapperEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO> : IShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>
    where EntityType : ShiftEntity<EntityType>
{
    private readonly IMapper mapper;
    private readonly ShiftEntityMappingContext? context;

    public ShiftMapperEntityMapper(IMapper mapper, ShiftEntityMappingContext? context)
    {
        this.mapper = mapper;
        this.context = context;
    }

    /// <summary>The mapper this adapter wraps.</summary>
    public IMapper Mapper => mapper;

    /// <summary>
    /// Whether <paramref name="mapper"/> declares all four maps of the triple. All four: a repository whose
    /// view reads but whose list does not project is a first-request failure, and the point of asking is to
    /// fail at startup instead.
    /// </summary>
    public static bool Covers(IMapper mapper) =>
        mapper.CanMap(typeof(EntityType), typeof(ViewAndUpsertDTO))
        && mapper.CanMap(typeof(ViewAndUpsertDTO), typeof(EntityType))
        && mapper.CanMap(typeof(EntityType), typeof(ListDTO))
        && mapper.CanMap(typeof(EntityType), typeof(EntityType));

    public ViewAndUpsertDTO MapToView(EntityType entity, MappingContext context = default) =>
        mapper.Map<EntityType, ViewAndUpsertDTO>(entity);

    public EntityType MapToEntity(ViewAndUpsertDTO dto, EntityType existing, MappingContext context = default)
    {
        var previous = this.context?.Enter(context.ActionType);

        try
        {
            return mapper.Map<ViewAndUpsertDTO, EntityType>(dto, existing);
        }
        catch (ShiftMapperConversionException e)
        {
            throw InvalidValue(e);
        }
        finally
        {
            this.context?.Exit(previous);
        }
    }

    public IQueryable<ListDTO> MapToList(IQueryable<EntityType> query, MappingContext context = default) =>
        mapper.ProjectTo<EntityType, ListDTO>(query);

    public void CopyEntity(EntityType source, EntityType target, MappingContext context = default) =>
        mapper.Map<EntityType, EntityType>(source, target);

    // ShiftEntityCrudHandler catches ShiftEntityException around the upsert and emits the "Model Validation
    // Error" shape the MVC ModelState path produces; `For` is what a form binds an inline error to. The member
    // is the SOURCE member of the failed mapping — the DTO property the client sent, `Brand` for
    // "ProductDTO.Brand.Value -> Product.BrandID" — which is what the client can correct.
    private static ShiftEntityException InvalidValue(ShiftMapperConversionException e) =>
        new(
            new Message("Model Validation Error", $"'{e.SourceMember}' is not a valid {e.TargetType.Name}.")
            {
                For = e.SourceMember,
            },
            (int)HttpStatusCode.BadRequest);
}

/// <summary>
/// The scoped <see cref="IShiftEntityMappingContext"/>: written by <see cref="ShiftMapperEntityMapper{EntityType, ListDTO, ViewAndUpsertDTO}"/>
/// around a write map, read by whatever the map runs. Registered by <c>RegisterShiftRepositories</c>.
/// </summary>
public sealed class ShiftEntityMappingContext : IShiftEntityMappingContext
{
    public ActionTypes? ActionType { get; private set; }

    /// <summary>Sets the action for the map about to run and returns what was there, for <see cref="Exit"/>.</summary>
    internal ActionTypes? Enter(ActionTypes? actionType)
    {
        var previous = ActionType;
        ActionType = actionType;
        return previous;
    }

    /// <summary>Restores what <see cref="Enter"/> replaced — a map inside a map leaves the outer one's action in place.</summary>
    internal void Exit(ActionTypes? previous) => ActionType = previous;
}
