using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.EFCore.Tagging;

/// <summary>
/// Helpers for projecting taggable entities to their list DTOs without hand-writing the
/// <c>Tags</c> projection in every mapper.
/// </summary>
public static class TaggableProjectionExtensions
{
    /// <summary>
    /// Projects <paramref name="source"/> with <paramref name="projection"/> and automatically
    /// appends the framework's canonical <c>Tags</c> projection — so a taggable list mapper writes
    /// the DTO projection <b>without</b> any Tags code (and can't silently forget it). Any <c>Tags</c>
    /// binding the caller did write is replaced.
    /// <para>
    /// The <see cref="TagProjection.ToDto"/> body is spliced <i>inline</i> into the member-init, so
    /// the resulting expression is identical in shape to a hand-written
    /// <c>e =&gt; new TListDTO { …, Tags = e.Tags.Select(t =&gt; new TagDTO { … }).ToList() }</c> and
    /// translates in EF Core exactly the same way (no referenced-Expression / LINQKit needed).
    /// </para>
    /// </summary>
    /// <param name="projection">A member-initializer projection: <c>e =&gt; new TListDTO { … }</c>.</param>
    public static IQueryable<TListDTO> SelectWithTags<TEntity, TListDTO>(
        this IQueryable<TEntity> source,
        Expression<Func<TEntity, TListDTO>> projection)
        where TEntity : IShiftEntityTaggable
        where TListDTO : IShiftEntityTaggableDTO
    {
        if (projection.Body is not MemberInitExpression init)
            throw new ArgumentException(
                $"{nameof(SelectWithTags)} requires a member-initializer projection, e.g. " +
                $"e => new {typeof(TListDTO).Name} {{ ... }}.",
                nameof(projection));

        var entity = projection.Parameters[0];

        // Inline TagProjection.ToDto under a fresh parameter: t => new TagDTO { ... }
        var tag = Expression.Parameter(typeof(Tag), "t");
        var tagBody = ParameterReplacer.Replace(TagProjection.ToDto.Body, TagProjection.ToDto.Parameters[0], tag);
        var tagSelector = Expression.Lambda<Func<Tag, TagDTO>>(tagBody, tag);

        // entity.Tags.Select(t => new TagDTO { ... }).ToList()
        var tagsNav = Expression.Property(entity, nameof(IShiftEntityTaggable.Tags));
        var select = Expression.Call(
            typeof(Enumerable), nameof(Enumerable.Select), new[] { typeof(Tag), typeof(TagDTO) },
            tagsNav, tagSelector);
        var toList = Expression.Call(
            typeof(Enumerable), nameof(Enumerable.ToList), new[] { typeof(TagDTO) },
            select);

        var tagsProp = typeof(TListDTO).GetProperty(nameof(IShiftEntityTaggableDTO.Tags))
            ?? throw new InvalidOperationException(
                $"{typeof(TListDTO).Name} has no '{nameof(IShiftEntityTaggableDTO.Tags)}' property.");

        // Drop any Tags binding the caller wrote, then append the framework's.
        var bindings = init.Bindings
            .Where(b => b.Member.Name != nameof(IShiftEntityTaggableDTO.Tags))
            .Append(Expression.Bind(tagsProp, toList));

        var merged = Expression.Lambda<Func<TEntity, TListDTO>>(
            Expression.MemberInit(init.NewExpression, bindings), entity);

        return source.Select(merged);
    }

    private sealed class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression from;
        private readonly ParameterExpression to;

        private ParameterReplacer(ParameterExpression from, ParameterExpression to)
        {
            this.from = from;
            this.to = to;
        }

        public static Expression Replace(Expression body, ParameterExpression from, ParameterExpression to)
            => new ParameterReplacer(from, to).Visit(body);

        protected override Expression VisitParameter(ParameterExpression node)
            => node == from ? to : base.VisitParameter(node);
    }
}
