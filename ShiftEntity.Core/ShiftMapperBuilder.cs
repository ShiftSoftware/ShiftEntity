using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Per-property customization for SOURCE-GENERATED mappers. Registered from the partial mapper class's
/// <c>Configure</c> hook and/or the repository's <c>UseGeneratedMapper(configure)</c> callback — the
/// repository callback runs later, so it wins when both customize the same member. Registering a member
/// automatically suppresses the generated convention for it (the generated code guards every convention
/// assignment with a lookup here).
///
/// Value delegates receive the <see cref="IServiceProvider"/> the mapping methods were called with, so
/// services are resolved at map time (scoped-safe). <see cref="ForList"/> takes an expression over the
/// entity because it is composed into the single SQL-translatable list projection; it may close over
/// values computed at configure time (they become SQL parameters).
/// </summary>
public class ShiftMapperBuilder<TEntity, TListDTO, TViewDTO>
{
    private readonly Dictionary<string, object> viewValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> entityValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> copyValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (MemberInfo Member, LambdaExpression Value)> listValues = new(StringComparer.Ordinal);

    /// <summary>Customizes a view-DTO property in <c>MapToView</c> (in-memory; full C# + services).</summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> ForView<TProp>(
        Expression<Func<TViewDTO, TProp>> member, Func<TEntity, IServiceProvider?, TProp> value)
    {
        this.viewValues[MemberOf(member).Name] = value;
        return this;
    }

    /// <summary>Customizes a view-DTO property in <c>MapToView</c> (no-service overload).</summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> ForView<TProp>(
        Expression<Func<TViewDTO, TProp>> member, Func<TEntity, TProp> value)
        => ForView(member, (entity, _) => value(entity));

    /// <summary>Customizes an entity property in <c>MapToEntity</c> (the upsert direction).</summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> ForEntity<TProp>(
        Expression<Func<TEntity, TProp>> member, Func<TViewDTO, IServiceProvider?, TProp> value)
    {
        this.entityValues[MemberOf(member).Name] = value;
        return this;
    }

    /// <summary>Customizes an entity property in <c>MapToEntity</c> (no-service overload).</summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> ForEntity<TProp>(
        Expression<Func<TEntity, TProp>> member, Func<TViewDTO, TProp> value)
        => ForEntity(member, (dto, _) => value(dto));

    /// <summary>Customizes an entity property in <c>CopyEntity</c> (value computed from the fresh source entity).</summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> ForCopy<TProp>(
        Expression<Func<TEntity, TProp>> member, Func<TEntity, IServiceProvider?, TProp> value)
    {
        this.copyValues[MemberOf(member).Name] = value;
        return this;
    }

    /// <summary>Customizes an entity property in <c>CopyEntity</c> (no-service overload).</summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> ForCopy<TProp>(
        Expression<Func<TEntity, TProp>> member, Func<TEntity, TProp> value)
        => ForCopy(member, (source, _) => value(source));

    /// <summary>
    /// Customizes a list-DTO property in <c>MapToList</c>. Must be an expression over the entity —
    /// it is spliced into the single generated projection, so it runs in SQL (full entity access,
    /// OData/paging unaffected).
    /// </summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> ForList<TProp>(
        Expression<Func<TListDTO, TProp>> member, Expression<Func<TEntity, TProp>> value)
    {
        var memberInfo = MemberOf(member);
        this.listValues[memberInfo.Name] = (memberInfo, value);
        return this;
    }

    // ─── consumed by the generated code ───

    public bool TryGetViewValue(string memberName, out object? value) => this.viewValues.TryGetValue(memberName, out value);

    public bool TryGetEntityValue(string memberName, out object? value) => this.entityValues.TryGetValue(memberName, out value);

    public bool TryGetCopyValue(string memberName, out object? value) => this.copyValues.TryGetValue(memberName, out value);

    /// <summary>
    /// Merges the <see cref="ForList"/> registrations into the generated member-init projection:
    /// customized members' bindings are replaced (their conventions never run), other bindings are kept,
    /// and the result is still one pure member-init lambda. Returns the projection unchanged when there
    /// are no list customizations.
    /// </summary>
    public Expression<Func<TEntity, TListDTO>> ComposeList(Expression<Func<TEntity, TListDTO>> projection)
    {
        if (this.listValues.Count == 0)
            return projection;

        if (projection.Body is not MemberInitExpression init)
            throw new InvalidOperationException(
                "ComposeList requires a member-initializer projection, e.g. e => new ListDTO { ... }.");

        var parameter = projection.Parameters[0];

        var bindings = init.Bindings
            .Where(b => !this.listValues.ContainsKey(b.Member.Name))
            .ToList();

        foreach (var custom in this.listValues.Values)
        {
            var body = new ParameterReplacer(custom.Value.Parameters[0], parameter).Visit(custom.Value.Body)!;
            bindings.Add(Expression.Bind(custom.Member, body));
        }

        return Expression.Lambda<Func<TEntity, TListDTO>>(Expression.MemberInit(init.NewExpression, bindings), parameter);
    }

    private static MemberInfo MemberOf(LambdaExpression selector)
    {
        if (selector.Body is MemberExpression member && member.Expression == selector.Parameters[0])
            return member.Member;

        throw new ArgumentException("The member selector must be a simple property access, e.g. d => d.Name.", nameof(selector));
    }

    private sealed class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression from;
        private readonly ParameterExpression to;

        public ParameterReplacer(ParameterExpression from, ParameterExpression to)
        {
            this.from = from;
            this.to = to;
        }

        protected override Expression VisitParameter(ParameterExpression node)
            => node == this.from ? this.to : base.VisitParameter(node);
    }
}
