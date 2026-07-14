using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.Core;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Direction-scoped child builders.
//
// The nested callback of a deep-mapping call (ForViewChild(ren) / ForListChild(ren) /
// ForEntityChild(ren)) hands you ONE of these instead of the full ShiftMapperBuilder. The parent
// call already fixed the direction, so the child surface is deliberately just FOUR members —
// Ignore / For / ForChild / ForChildren — with the direction-correct signature baked in:
//
//   • view  → For takes an in-memory Func (full C# + services)         [ShiftViewChildMapper]
//   • list  → For takes a SQL-translatable Expression over the entity  [ShiftListChildMapper]
//   • entity→ For takes an in-memory Func from the DTO (upsert)         [ShiftEntityChildMapper]
//
// This removes the old footgun where the nested builder exposed all four directions but only the
// one matching the parent had any effect (e.g. `child.ForList(...)` inside a ForViewChildren was a
// silent no-op). Each is a thin wrapper over a ShiftMapperBuilder<TEntity, TDto, TDto> — the same
// runtime machinery (AddConfiguration / ComposeList / pair resolution) as before; only the surface
// the programmer sees changed. The source generator reads these calls at build time by the wrapper
// TYPE (the type name carries the direction), so nested customization is still baked identically.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The child surface for the VIEW direction (inside a <c>ForViewChild</c>/<c>ForViewChildren</c>
/// callback). Customizes the composed child's <c>MapToView</c>. Exactly four members: <see cref="Ignore"/>,
/// <see cref="For{TProp}(Expression{Func{TDto, TProp}}, Func{TEntity, MappingContext, TProp})"/>,
/// <see cref="ForChild"/>, <see cref="ForChildren"/> — the direction is fixed by the parent call.
/// </summary>
public sealed class ShiftViewChildMapper<TEntity, TDto>
{
    private readonly ShiftMapperBuilder<TEntity, TDto, TDto> inner;
    internal ShiftViewChildMapper(ShiftMapperBuilder<TEntity, TDto, TDto> inner) => this.inner = inner;

    /// <summary>Customizes a child DTO property in the view direction (in-memory; full C# + services).</summary>
    public ShiftViewChildMapper<TEntity, TDto> For<TProp>(
        Expression<Func<TDto, TProp>> member, Func<TEntity, MappingContext, TProp> value)
    {
        this.inner.ForView(member, value);
        return this;
    }

    /// <summary>Customizes a child DTO property in the view direction (no-service overload).</summary>
    public ShiftViewChildMapper<TEntity, TDto> For<TProp>(
        Expression<Func<TDto, TProp>> member, Func<TEntity, TProp> value)
    {
        this.inner.ForView(member, value);
        return this;
    }

    /// <summary>Excludes a child member from the composed view mapping. Build-time marker.</summary>
    public ShiftViewChildMapper<TEntity, TDto> Ignore<TProp>(Expression<Func<TDto, TProp>> member)
    {
        this.inner.IgnoreView(member);
        return this;
    }

    /// <summary>Composes a single grandchild object in the view direction (null-safe), one level deeper.</summary>
    public ShiftViewChildMapper<TEntity, TDto> ForChild<TChildEntity, TChildDto>(
        Expression<Func<TDto, TChildDto?>> member, Func<TEntity, TChildEntity?> from,
        Action<ShiftViewChildMapper<TChildEntity, TChildDto>>? configure = null)
        where TChildEntity : class
        where TChildDto : class
    {
        this.inner.ForViewChild(member, from, configure);
        return this;
    }

    /// <summary>Composes a grandchild collection in the view direction, one level deeper.</summary>
    public ShiftViewChildMapper<TEntity, TDto> ForChildren<TChildEntity, TChildDto>(
        Expression<Func<TDto, IEnumerable<TChildDto>?>> member, Func<TEntity, IEnumerable<TChildEntity>?> from,
        Action<ShiftViewChildMapper<TChildEntity, TChildDto>>? configure = null)
        where TChildDto : class
    {
        this.inner.ForViewChildren(member, from, configure);
        return this;
    }
}

/// <summary>
/// The child surface for the LIST direction (inside a <c>ForListChild</c>/<c>ForListChildren</c>
/// callback). Customizes the composed child inside the single SQL-translatable list projection.
/// <see cref="For{TProp}"/> takes an <see cref="Expression"/> over the entity (spliced into the SQL);
/// the other three mirror the view surface. Direction is fixed by the parent call.
/// </summary>
public sealed class ShiftListChildMapper<TEntity, TDto>
{
    private readonly ShiftMapperBuilder<TEntity, TDto, TDto> inner;
    internal ShiftListChildMapper(ShiftMapperBuilder<TEntity, TDto, TDto> inner) => this.inner = inner;

    /// <summary>Customizes a child DTO property in the list projection. Must be an expression over the entity (runs in SQL).</summary>
    public ShiftListChildMapper<TEntity, TDto> For<TProp>(
        Expression<Func<TDto, TProp>> member, Expression<Func<TEntity, TProp>> value)
    {
        this.inner.ForList(member, value);
        return this;
    }

    /// <summary>Excludes a child member from the composed list projection. Build-time marker.</summary>
    public ShiftListChildMapper<TEntity, TDto> Ignore<TProp>(Expression<Func<TDto, TProp>> member)
    {
        this.inner.IgnoreList(member);
        return this;
    }

    /// <summary>Composes a single grandchild object in the list projection (null-safe), one level deeper.</summary>
    public ShiftListChildMapper<TEntity, TDto> ForChild<TChildEntity, TChildDto>(
        Expression<Func<TDto, TChildDto>> member, Expression<Func<TEntity, TChildEntity>> source,
        Action<ShiftListChildMapper<TChildEntity, TChildDto>>? configure = null)
        where TChildEntity : class
        where TChildDto : class
    {
        this.inner.ForListChild(member, source, configure);
        return this;
    }

    /// <summary>Composes a grandchild collection in the list projection (correlated sub-select), one level deeper.</summary>
    public ShiftListChildMapper<TEntity, TDto> ForChildren<TChildEntity, TChildDto>(
        Expression<Func<TDto, IEnumerable<TChildDto>>> member, Expression<Func<TEntity, IEnumerable<TChildEntity>>> source,
        Action<ShiftListChildMapper<TChildEntity, TChildDto>>? configure = null)
    {
        this.inner.ForListChildren(member, source, configure);
        return this;
    }
}

/// <summary>
/// The child surface for the ENTITY (upsert) direction (inside a <c>ForEntityChild</c>/<c>ForEntityChildren</c>
/// callback). Customizes how the composed child DTO is written back onto a fresh entity (replace-with-new).
/// Member selectors are over the ENTITY; values come from the DTO. Direction is fixed by the parent call.
/// </summary>
public sealed class ShiftEntityChildMapper<TEntity, TDto>
{
    private readonly ShiftMapperBuilder<TEntity, TDto, TDto> inner;
    internal ShiftEntityChildMapper(ShiftMapperBuilder<TEntity, TDto, TDto> inner) => this.inner = inner;

    /// <summary>Customizes a child entity property in the upsert direction (value computed from the DTO + services).</summary>
    public ShiftEntityChildMapper<TEntity, TDto> For<TProp>(
        Expression<Func<TEntity, TProp>> member, Func<TDto, MappingContext, TProp> value)
    {
        this.inner.ForEntity(member, value);
        return this;
    }

    /// <summary>Customizes a child entity property in the upsert direction (no-service overload).</summary>
    public ShiftEntityChildMapper<TEntity, TDto> For<TProp>(
        Expression<Func<TEntity, TProp>> member, Func<TDto, TProp> value)
    {
        this.inner.ForEntity(member, value);
        return this;
    }

    /// <summary>Excludes a child member from the upsert mapping. Build-time marker.</summary>
    public ShiftEntityChildMapper<TEntity, TDto> Ignore<TProp>(Expression<Func<TEntity, TProp>> member)
    {
        this.inner.IgnoreEntity(member);
        return this;
    }

    /// <summary>Writes a single grandchild object back (into a NEW instance), one level deeper.</summary>
    public ShiftEntityChildMapper<TEntity, TDto> ForChild<TChildEntity, TChildDto>(
        Expression<Func<TEntity, TChildEntity>> member, Func<TDto, TChildDto?> from,
        Action<ShiftEntityChildMapper<TChildEntity, TChildDto>>? configure = null)
        where TChildEntity : class, new()
        where TChildDto : class
    {
        this.inner.ForEntityChild(member, from, configure);
        return this;
    }

    /// <summary>Writes a grandchild collection back (replace-with-new via the pair mapper), one level deeper.</summary>
    public ShiftEntityChildMapper<TEntity, TDto> ForChildren<TChildEntity, TChildDto>(
        Expression<Func<TEntity, List<TChildEntity>>> member, Func<TDto, IEnumerable<TChildDto>?> from,
        Action<ShiftEntityChildMapper<TChildEntity, TChildDto>>? configure = null)
        where TChildEntity : class, new()
    {
        this.inner.ForEntityChildren(member, from, configure);
        return this;
    }

    /// <inheritdoc cref="ForChildren{TChildEntity, TChildDto}(Expression{Func{TEntity, List{TChildEntity}}}, Func{TDto, IEnumerable{TChildDto}}, Action{ShiftEntityChildMapper{TChildEntity, TChildDto}})"/>
    public ShiftEntityChildMapper<TEntity, TDto> ForChildren<TChildEntity, TChildDto>(
        Expression<Func<TEntity, ICollection<TChildEntity>>> member, Func<TDto, IEnumerable<TChildDto>?> from,
        Action<ShiftEntityChildMapper<TChildEntity, TChildDto>>? configure = null)
        where TChildEntity : class, new()
    {
        this.inner.ForEntityChildren(member, from, configure);
        return this;
    }
}
