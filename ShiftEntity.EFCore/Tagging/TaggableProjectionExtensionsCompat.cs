using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;
using System;
using System.Linq;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.EFCore.Tagging;

/// <summary>
/// Compatibility shim. The implementation moved to
/// <see cref="ShiftSoftware.ShiftEntity.Core.Tagging.TaggableProjectionExtensions"/> so the source generator —
/// which ships inside <c>ShiftEntity.Core</c> — no longer emits a call into <c>ShiftEntity.EFCore</c>. That
/// layering inversion meant a Core-only project with a taggable entity got generated source that did not
/// compile.
/// <para>
/// This stays for one release because mappers baked into already-published downstream packages call THIS
/// signature by name, and a generated mapper is code frozen at the dependency's build day (gap B-10) — removing
/// it outright would be a <c>MissingMethodException</c> at request time under version skew, not a compile error.
/// Extension-ness is not part of the IL signature, so this satisfies the baked static call and keeps existing
/// extension-method usage compiling.
/// </para>
/// </summary>
[Obsolete("Moved to ShiftSoftware.ShiftEntity.Core.Tagging.TaggableProjectionExtensions. This shim exists only for mappers baked into packages built before the move.")]
public static class TaggableProjectionExtensions
{
    /// <inheritdoc cref="ShiftSoftware.ShiftEntity.Core.Tagging.TaggableProjectionExtensions.SelectWithTags{TEntity, TListDTO}(IQueryable{TEntity}, Expression{Func{TEntity, TListDTO}})"/>
    public static IQueryable<TListDTO> SelectWithTags<TEntity, TListDTO>(
        this IQueryable<TEntity> source,
        Expression<Func<TEntity, TListDTO>> projection)
        where TEntity : IShiftEntityTaggable
        where TListDTO : IShiftEntityTaggableDTO
        => Core.Tagging.TaggableProjectionExtensions.SelectWithTags(source, projection);
}
