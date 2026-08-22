using System;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Non-generic marker for the mapper family. Its only purpose is to let generic parameters be
/// constrained to "a ShiftEntity mapper" without knowing the entity/DTO type arguments — e.g. the
/// <c>TMapper</c> of <c>ShiftEntityEndpointWithMapperAttribute&lt;…, TMapper&gt;</c>. The exact
/// <c>(entity, list, view)</c> triple is validated separately (at endpoint discovery).
/// </summary>
public interface IShiftEntityMapper { }

public interface IShiftEntityMapper<TEntity, TListDTO, TViewDTO> : IShiftEntityMapper
{
    // Each method receives a MappingContext carrying the service provider (so a mapper can resolve services
    // on demand — a lookup/localization service — instead of constructor-injecting them) plus the action
    // being performed when known. This lets a mapper stay unregistered — plugged via
    // options.UseMapper(new MyMapper()) — yet still reach DI. The repository passes its DbContext's
    // application service provider when it calls these; a bare IServiceProvider converts implicitly.
    TViewDTO MapToView(TEntity entity, MappingContext context = default);
    TEntity MapToEntity(TViewDTO dto, TEntity existing, MappingContext context = default);

    /// <summary>
    /// Projects the entity queryable onto the list DTO. Must stay an <b>expression</b> the database can
    /// translate — a member-init <c>Select</c>, no helper calls inside the lambda — because everything the OData
    /// pipeline does runs <i>after</i> this projection.
    /// <para>
    /// <b>Two members are mandatory, and omitting either takes the endpoint down:</b>
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b><c>IsDeleted</c></b> — the Web layer appends <c>.Where(x =&gt; !x.IsDeleted)</c> to the
    ///     <i>already-projected DTO</i> queryable, not to the entity queryable. If the projection never binds it,
    ///     there is nothing for that predicate to reach.
    ///   </item>
    ///   <item>
    ///     <b><c>ID</c></b> — hash-id <c>$filter</c> rewriting and <c>$orderby</c> also run against the projected
    ///     DTO.
    ///   </item>
    /// </list>
    /// <para>
    /// On EF Core an unbound member does not quietly default: the query fails to translate and the request
    /// throws. So a missing <c>IsDeleted</c> is a 500 on every list call, and a missing <c>ID</c> is a 500 the
    /// first time anyone sorts or filters by it — which is the shape that reaches production, because the grid
    /// works until someone touches a column header. (In LINQ-to-Objects, e.g. a unit test over an array, the same
    /// omission is silent and returns soft-deleted rows instead.)
    /// </para>
    /// <para>
    /// The source generator binds both automatically. This is a contract for <i>hand-written</i> mappers and for
    /// repositories that override <c>MapToList</c>. <c>MappingHelpers.MapBaseListFields</c> cannot help here — it
    /// is an in-memory call and no projection can translate it; bind the two members inline.
    /// </para>
    /// </summary>
    IQueryable<TListDTO> MapToList(IQueryable<TEntity> query, MappingContext context = default);

    void CopyEntity(TEntity source, TEntity target, MappingContext context = default);
}
