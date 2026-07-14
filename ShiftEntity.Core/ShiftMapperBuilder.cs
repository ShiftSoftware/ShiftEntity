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
    private readonly Dictionary<string, (MemberInfo Member, LambdaExpression Source, Type ChildEntity, Type ChildDto, bool IsCollection, Func<LambdaExpression> Projection)> listChildren = new(StringComparer.Ordinal);

    // Build-time markers. The source generator reads Ignore/MaxDepth from the CALL SYNTAX at compile time
    // (it bakes the decision into the generated code); the runtime records are kept only so the fluent
    // calls compile and so the dynamic-config opt-out path can honour them.
    private readonly HashSet<string> ignoredView = new(StringComparer.Ordinal);
    private readonly HashSet<string> ignoredEntity = new(StringComparer.Ordinal);
    private readonly HashSet<string> ignoredList = new(StringComparer.Ordinal);
    private readonly HashSet<string> ignoredCopy = new(StringComparer.Ordinal);

    private static readonly MethodInfo EnumerableSelect = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.Select) && m.GetParameters().Length == 2 &&
                    m.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(Func<,>));

    private static readonly MethodInfo EnumerableToList = typeof(Enumerable).GetMethod(nameof(Enumerable.ToList))!;

    /// <summary>Customizes a view-DTO property in <c>MapToView</c> (in-memory; full C# + services).</summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> ForView<TProp>(
        Expression<Func<TViewDTO, TProp>> member, Func<TEntity, MappingContext, TProp> value)
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
        Expression<Func<TEntity, TProp>> member, Func<TViewDTO, MappingContext, TProp> value)
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
        Expression<Func<TEntity, TProp>> member, Func<TEntity, MappingContext, TProp> value)
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

    // ─── ignore + depth: build-time markers the generator reads from the call syntax ───

    /// <summary>
    /// Excludes a member from generated mapping in ALL four directions (the member is OMITTED from the
    /// generated bodies; a complex child's deep subtree is pruned). Build-time marker — the generator
    /// reads this call. The selector is a view-DTO property; the member NAME is matched across directions.
    /// </summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> Ignore<TProp>(Expression<Func<TViewDTO, TProp>> member)
    {
        var name = MemberOf(member).Name;
        this.ignoredView.Add(name);
        this.ignoredEntity.Add(name);
        this.ignoredList.Add(name);
        this.ignoredCopy.Add(name);
        return this;
    }

    /// <summary>Excludes a member from <c>MapToView</c> only. Build-time marker.</summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> IgnoreView<TProp>(Expression<Func<TViewDTO, TProp>> member)
    {
        this.ignoredView.Add(MemberOf(member).Name);
        return this;
    }

    /// <summary>Excludes a member from <c>MapToEntity</c> only. Build-time marker.</summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> IgnoreEntity<TProp>(Expression<Func<TEntity, TProp>> member)
    {
        this.ignoredEntity.Add(MemberOf(member).Name);
        return this;
    }

    /// <summary>Excludes a member from <c>MapToList</c> only. Build-time marker.</summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> IgnoreList<TProp>(Expression<Func<TListDTO, TProp>> member)
    {
        this.ignoredList.Add(MemberOf(member).Name);
        return this;
    }

    /// <summary>Excludes a member from <c>CopyEntity</c> only. Build-time marker.</summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> IgnoreCopy<TProp>(Expression<Func<TEntity, TProp>> member)
    {
        this.ignoredCopy.Add(MemberOf(member).Name);
        return this;
    }

    /// <summary>
    /// Sets the maximum AUTOMATIC deep-mapping depth for this mapper (equivalent to
    /// <see cref="ShiftEntityMapperMaxDepthAttribute"/>). Build-time marker — the generator reads the CONSTANT
    /// argument of this call and bakes the cap into the generated code. Only automatic composition is bounded;
    /// explicit <c>ForXxxChild(ren)</c> composes beyond the cap. (A non-constant argument can't be baked.)
    /// </summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> MaxDepth(int depth) => this;

    // ─── deep-mapping sugar: wire the generated PAIR mappers explicitly, without boilerplate ───

    /// <summary>
    /// Maps a child COLLECTION in <c>MapToEntity</c> through the source-generated pair mapper.
    /// Semantics: REPLACE-WITH-NEW — every child DTO becomes a new entity instance (pair this with a
    /// repository that owns the previous children, e.g. delete-and-recreate); it is NOT a merge/update-by-ID.
    /// <para>
    /// Pass <paramref name="configureChild"/> to customize the child's write mapping (<c>child.ForEntity(...)</c>)
    /// or compose deeper children explicitly (<c>child.ForEntityChild(...)</c>) — same builder, any depth.
    /// </para>
    /// </summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> ForEntityChildren<TChildEntity, TChildDto>(
        Expression<Func<TEntity, List<TChildEntity>>> member, Func<TViewDTO, IEnumerable<TChildDto>?> from,
        Action<ShiftMapperBuilder<TChildEntity, TChildDto, TChildDto>>? configureChild = null)
        where TChildEntity : class, new()
    {
        var pair = ResolvePair<TChildEntity, TChildDto>();
        ApplyChildConfig(pair, configureChild);

        this.entityValues[MemberOf(member).Name] = new Func<TViewDTO, MappingContext, List<TChildEntity>?>((dto, sp) =>
        {
            var source = from(dto);
            return source == null ? null : source.Select(c => pair.MapBack(c, new TChildEntity(), sp)).ToList();
        });

        return this;
    }

    /// <inheritdoc cref="ForEntityChildren{TChildEntity, TChildDto}(Expression{Func{TEntity, List{TChildEntity}}}, Func{TViewDTO, IEnumerable{TChildDto}}, Action{ShiftMapperBuilder{TChildEntity, TChildDto, TChildDto}})"/>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> ForEntityChildren<TChildEntity, TChildDto>(
        Expression<Func<TEntity, ICollection<TChildEntity>>> member, Func<TViewDTO, IEnumerable<TChildDto>?> from,
        Action<ShiftMapperBuilder<TChildEntity, TChildDto, TChildDto>>? configureChild = null)
        where TChildEntity : class, new()
    {
        var pair = ResolvePair<TChildEntity, TChildDto>();
        ApplyChildConfig(pair, configureChild);

        this.entityValues[MemberOf(member).Name] = new Func<TViewDTO, MappingContext, ICollection<TChildEntity>?>((dto, sp) =>
        {
            var source = from(dto);
            return source == null ? null : source.Select(c => pair.MapBack(c, new TChildEntity(), sp)).ToList();
        });

        return this;
    }

    /// <summary>
    /// Maps a single child object in <c>MapToEntity</c> through the source-generated pair mapper (into a NEW
    /// instance). Pass <paramref name="configureChild"/> to customize the child or compose deeper (see
    /// <see cref="ForEntityChildren{TChildEntity, TChildDto}(Expression{Func{TEntity, List{TChildEntity}}}, Func{TViewDTO, IEnumerable{TChildDto}}, Action{ShiftMapperBuilder{TChildEntity, TChildDto, TChildDto}})"/>).
    /// </summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> ForEntityChild<TChildEntity, TChildDto>(
        Expression<Func<TEntity, TChildEntity>> member, Func<TViewDTO, TChildDto?> from,
        Action<ShiftMapperBuilder<TChildEntity, TChildDto, TChildDto>>? configureChild = null)
        where TChildEntity : class, new()
        where TChildDto : class
    {
        var pair = ResolvePair<TChildEntity, TChildDto>();
        ApplyChildConfig(pair, configureChild);

        this.entityValues[MemberOf(member).Name] = new Func<TViewDTO, MappingContext, TChildEntity?>((dto, sp) =>
        {
            var source = from(dto);
            return source == null ? null : pair.MapBack(source, new TChildEntity(), sp);
        });

        return this;
    }

    /// <summary>
    /// Composes a child COLLECTION in <c>MapToView</c> through the source-generated pair mapper — EXPLICITLY.
    /// In the view direction nothing goes deep automatically: a child object is composed only when you call
    /// this. Pass <paramref name="configureChild"/> to customize a child property (<c>child.ForView(...)</c>)
    /// or compose deeper children (<c>child.ForViewChild(...)</c>) — same builder, any depth.
    /// </summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> ForViewChildren<TChildEntity, TChildDto>(
        Expression<Func<TViewDTO, IEnumerable<TChildDto>?>> member, Func<TEntity, IEnumerable<TChildEntity>?> from,
        Action<ShiftMapperBuilder<TChildEntity, TChildDto, TChildDto>>? configureChild = null)
        where TChildDto : class
    {
        var pair = ResolvePair<TChildEntity, TChildDto>();
        ApplyChildConfig(pair, configureChild);

        this.viewValues[MemberOf(member).Name] = new Func<TEntity, MappingContext, List<TChildDto>?>((entity, sp) =>
        {
            var source = from(entity);
            return source == null ? null : source.Select(c => pair.Map(c, sp)).ToList();
        });

        return this;
    }

    /// <summary>Composes a single child object in <c>MapToView</c> through the source-generated pair mapper (null-safe), EXPLICITLY. Pass <paramref name="configureChild"/> to customize or compose deeper (see <see cref="ForViewChildren"/>).</summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> ForViewChild<TChildEntity, TChildDto>(
        Expression<Func<TViewDTO, TChildDto?>> member, Func<TEntity, TChildEntity?> from,
        Action<ShiftMapperBuilder<TChildEntity, TChildDto, TChildDto>>? configureChild = null)
        where TChildEntity : class
        where TChildDto : class
    {
        var pair = ResolvePair<TChildEntity, TChildDto>();
        ApplyChildConfig(pair, configureChild);

        this.viewValues[MemberOf(member).Name] = new Func<TEntity, MappingContext, TChildDto?>((entity, sp) =>
        {
            var source = from(entity);
            return source == null ? null : pair.Map(source, sp);
        });

        return this;
    }

    /// <summary>Applies a nested child configuration to the child pair mapper (its MapToView/MapToEntity honour it via the same builder). Requires a configurable — i.e. source-generated or [ShiftEntityMapper] — pair.</summary>
    private static void ApplyChildConfig<TChildEntity, TChildDto>(
        IShiftObjectMapper<TChildEntity, TChildDto> pair, Action<ShiftMapperBuilder<TChildEntity, TChildDto, TChildDto>>? configureChild)
    {
        if (configureChild is null)
            return;

        if (pair is not IShiftMapperConfigurable<TChildEntity, TChildDto, TChildDto> configurable)
            throw new InvalidOperationException(
                $"The pair mapper for ({typeof(TChildEntity).Name}, {typeof(TChildDto).Name}) is not configurable; " +
                "nested customization needs a source-generated or [ShiftEntityMapper] pair.");

        configurable.AddConfiguration(configureChild);
    }

    /// <summary>
    /// Projects a child COLLECTION inside the <c>MapToList</c> SQL projection, using the pair's
    /// source-generated, conventions-only projection expression (a correlated collection query —
    /// deliberate, visible query-shape change). For a custom child list shape use <see cref="ForList"/>.
    /// <para>
    /// Pass <paramref name="configureChild"/> to shape the child EXPLICITLY: the callback receives a
    /// builder for the child pair, so you customize a child property (<c>child.ForList(...)</c>) or
    /// compose ONE LEVEL DEEPER (<c>child.ForListChild(...)</c>) — same builder, any depth. A child
    /// object is only composed when you say so; nothing goes deep automatically in the list direction.
    /// </para>
    /// </summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> ForListChildren<TChildEntity, TChildDto>(
        Expression<Func<TListDTO, IEnumerable<TChildDto>>> member, Expression<Func<TEntity, IEnumerable<TChildEntity>>> source,
        Action<ShiftMapperBuilder<TChildEntity, TChildDto, TChildDto>>? configureChild = null)
    {
        var memberInfo = MemberOf(member);
        this.listChildren[memberInfo.Name] = (memberInfo, source, typeof(TChildEntity), typeof(TChildDto), true,
            () => ComposedChildProjection(configureChild));
        return this;
    }

    /// <summary>
    /// Projects a single child object inside the <c>MapToList</c> SQL projection (null-safe), using the
    /// pair's generated projection. Pass <paramref name="configureChild"/> to customize the child's
    /// properties or compose deeper children explicitly (see <see cref="ForListChildren"/>).
    /// </summary>
    public ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> ForListChild<TChildEntity, TChildDto>(
        Expression<Func<TListDTO, TChildDto>> member, Expression<Func<TEntity, TChildEntity>> source,
        Action<ShiftMapperBuilder<TChildEntity, TChildDto, TChildDto>>? configureChild = null)
        where TChildEntity : class
        where TChildDto : class
    {
        var memberInfo = MemberOf(member);
        this.listChildren[memberInfo.Name] = (memberInfo, source, typeof(TChildEntity), typeof(TChildDto), false,
            () => ComposedChildProjection(configureChild));
        return this;
    }

    private static IShiftObjectMapper<TChildEntity, TChildDto> ResolvePair<TChildEntity, TChildDto>()
    {
        var mapperType = ShiftEntityMapperRegistry.FindPair(typeof(TChildEntity), typeof(TChildDto))
            ?? throw new InvalidOperationException(
                $"No source-generated pair mapper is registered for ({typeof(TChildEntity).Name}, {typeof(TChildDto).Name}). " +
                "Ensure the ShiftEntity source generator runs on the assembly (pairs are discovered from view DTOs automatically), " +
                "or declare a [ShiftEntityMapper] partial class implementing IShiftObjectMapper for this exact pair.");

        return (IShiftObjectMapper<TChildEntity, TChildDto>)Activator.CreateInstance(mapperType)!;
    }

    /// <summary>
    /// The child pair's list projection, optionally CUSTOMIZED by <paramref name="configureChild"/>. With
    /// no callback this is the source-generated conventions-only projection. With one, the child's
    /// customizations and its own (explicit) deeper ForListChild(ren) are composed into it via the child
    /// builder's <see cref="ComposeList"/> — so the whole thing is one SQL-translatable expression tree,
    /// recursively, to whatever depth was configured.
    /// </summary>
    private static LambdaExpression ComposedChildProjection<TChildEntity, TChildDto>(
        Action<ShiftMapperBuilder<TChildEntity, TChildDto, TChildDto>>? configureChild)
    {
        var projection = ShiftEntityMapperRegistry.FindPairListProjection(typeof(TChildEntity), typeof(TChildDto))
            ?? throw new InvalidOperationException(
                $"No pair list projection is registered for ({typeof(TChildEntity).Name}, {typeof(TChildDto).Name}). " +
                "Ensure the ShiftEntity source generator runs on the assembly declaring the pair (a ForListChild(ren) call opts it in).");

        if (configureChild is null)
            return projection;

        var childBuilder = new ShiftMapperBuilder<TChildEntity, TChildDto, TChildDto>();
        configureChild(childBuilder);
        return childBuilder.ComposeList((Expression<Func<TChildEntity, TChildDto>>)projection);
    }

    // ─── consumed by the generated code (not part of the customization API) ───

    /// <summary>
    /// Resolves a view-DTO member: the registered <c>ForView</c> customization if one exists, otherwise
    /// the generated <paramref name="convention"/>. The convention is a delegate, so it runs ONLY when the
    /// member is not customized (guard-before-execute — a customized convention that would throw is skipped).
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public TProp ResolveView<TProp>(TEntity entity, MappingContext serviceProvider, string memberName, Func<TEntity, MappingContext, TProp> convention)
        => this.viewValues.TryGetValue(memberName, out var custom)
            ? ((Func<TEntity, MappingContext, TProp>)custom)(entity, serviceProvider)
            : convention(entity, serviceProvider);

    /// <summary>Resolves an entity member in <c>MapToEntity</c>: the <c>ForEntity</c> customization, else the generated convention.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public TProp ResolveEntity<TProp>(TViewDTO dto, MappingContext serviceProvider, string memberName, Func<TViewDTO, MappingContext, TProp> convention)
        => this.entityValues.TryGetValue(memberName, out var custom)
            ? ((Func<TViewDTO, MappingContext, TProp>)custom)(dto, serviceProvider)
            : convention(dto, serviceProvider);

    /// <summary>Resolves an entity member in <c>CopyEntity</c>: the <c>ForCopy</c> customization, else the generated convention.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public TProp ResolveCopy<TProp>(TEntity source, MappingContext serviceProvider, string memberName, Func<TEntity, MappingContext, TProp> convention)
        => this.copyValues.TryGetValue(memberName, out var custom)
            ? ((Func<TEntity, MappingContext, TProp>)custom)(source, serviceProvider)
            : convention(source, serviceProvider);

    // Value-fallback overloads for customization-only members (base/audit/navigation/excluded — no
    // convention): apply the customization if present, otherwise keep the value that is already there
    // (what MapBaseFields set, or the existing/target value). Lets the generator emit ONE line with no
    // if-guard for these members too.

    /// <summary>View member with no convention: the <c>ForView</c> customization if present, else <paramref name="current"/> (unchanged).</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public TProp ResolveView<TProp>(TEntity entity, MappingContext serviceProvider, string memberName, TProp current)
        => this.viewValues.TryGetValue(memberName, out var custom)
            ? ((Func<TEntity, MappingContext, TProp>)custom)(entity, serviceProvider)
            : current;

    /// <summary>Entity member with no convention: the <c>ForEntity</c> customization if present, else <paramref name="current"/> (unchanged).</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public TProp ResolveEntity<TProp>(TViewDTO dto, MappingContext serviceProvider, string memberName, TProp current)
        => this.entityValues.TryGetValue(memberName, out var custom)
            ? ((Func<TViewDTO, MappingContext, TProp>)custom)(dto, serviceProvider)
            : current;

    /// <summary>Copy member with no convention: the <c>ForCopy</c> customization if present, else <paramref name="current"/> (target unchanged).</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public TProp ResolveCopy<TProp>(TEntity source, MappingContext serviceProvider, string memberName, TProp current)
        => this.copyValues.TryGetValue(memberName, out var custom)
            ? ((Func<TEntity, MappingContext, TProp>)custom)(source, serviceProvider)
            : current;

    // ─── build-time-baked references: the generated code calls these for members the generator KNOWS are
    // customized (no per-member custom-vs-convention branch — the decision was made at compile time). The
    // customization VALUE stays a runtime delegate; only present customizations are invoked, otherwise the
    // member keeps whatever it already holds (the DTO/target default), so a mapper used without its config
    // never throws. ───

    /// <summary>Invokes the registered <c>ForView</c> customization for <paramref name="memberName"/>, or returns <paramref name="current"/> if none is registered.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public TProp InvokeView<TProp>(TEntity entity, MappingContext context, string memberName, TProp current = default!)
        => this.viewValues.TryGetValue(memberName, out var custom)
            ? ((Func<TEntity, MappingContext, TProp>)custom)(entity, context)
            : current;

    /// <summary>Invokes the registered <c>ForEntity</c> customization for <paramref name="memberName"/>, or returns <paramref name="current"/> if none is registered.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public TProp InvokeEntity<TProp>(TViewDTO dto, MappingContext context, string memberName, TProp current = default!)
        => this.entityValues.TryGetValue(memberName, out var custom)
            ? ((Func<TViewDTO, MappingContext, TProp>)custom)(dto, context)
            : current;

    /// <summary>Invokes the registered <c>ForCopy</c> customization for <paramref name="memberName"/>, or returns <paramref name="current"/> if none is registered.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public TProp InvokeCopy<TProp>(TEntity source, MappingContext context, string memberName, TProp current = default!)
        => this.copyValues.TryGetValue(memberName, out var custom)
            ? ((Func<TEntity, MappingContext, TProp>)custom)(source, context)
            : current;

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public bool TryGetViewValue(string memberName, out object? value) => this.viewValues.TryGetValue(memberName, out value);

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public bool TryGetEntityValue(string memberName, out object? value) => this.entityValues.TryGetValue(memberName, out value);

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public bool TryGetCopyValue(string memberName, out object? value) => this.copyValues.TryGetValue(memberName, out value);

    /// <summary>
    /// Merges the <see cref="ForList"/> registrations into the generated member-init projection:
    /// customized members' bindings are replaced (their conventions never run), other bindings are kept,
    /// and the result is still one pure member-init lambda. Returns the projection unchanged when there
    /// are no list customizations.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public Expression<Func<TEntity, TListDTO>> ComposeList(Expression<Func<TEntity, TListDTO>> projection)
    {
        if (this.listValues.Count == 0 && this.listChildren.Count == 0)
            return projection;

        if (projection.Body is not MemberInitExpression init)
            throw new InvalidOperationException(
                "ComposeList requires a member-initializer projection, e.g. e => new ListDTO { ... }.");

        var parameter = projection.Parameters[0];

        var replaced = new HashSet<string>(this.listValues.Keys, StringComparer.Ordinal);
        replaced.UnionWith(this.listChildren.Keys);

        var bindings = init.Bindings
            .Where(b => !replaced.Contains(b.Member.Name))
            .ToList();

        foreach (var custom in this.listValues.Values)
        {
            var body = new ParameterReplacer(custom.Value.Parameters[0], parameter).Visit(custom.Value.Body)!;
            bindings.Add(Expression.Bind(custom.Member, body));
        }

        foreach (var child in this.listChildren.Values)
        {
            // Static pair projection, or — when the ForListChild(ren) call passed a configureChild callback —
            // that projection with the child's own customizations and deeper children composed in (recursive).
            var childProjection = child.Projection();

            var sourceBody = new ParameterReplacer(child.Source.Parameters[0], parameter).Visit(child.Source.Body)!;

            Expression value;

            if (child.IsCollection)
            {
                // e.Children.Select(childProjection).ToList() — the compiler-equivalent shape of a
                // hand-written correlated collection projection; EF translates it as such.
                var select = Expression.Call(
                    EnumerableSelect.MakeGenericMethod(child.ChildEntity, child.ChildDto), sourceBody, childProjection);
                value = Expression.Call(EnumerableToList.MakeGenericMethod(child.ChildDto), select);
            }
            else
            {
                // Inline the pair projection's body over the source expression, null-safe.
                var inlined = new ParameterReplacer(childProjection.Parameters[0], sourceBody).Visit(childProjection.Body)!;
                value = Expression.Condition(
                    Expression.Equal(sourceBody, Expression.Constant(null, child.ChildEntity)),
                    Expression.Constant(null, child.ChildDto),
                    inlined);
            }

            bindings.Add(Expression.Bind(child.Member, value));
        }

        return Expression.Lambda<Func<TEntity, TListDTO>>(Expression.MemberInit(init.NewExpression, bindings), parameter);
    }

    private static MemberInfo MemberOf(LambdaExpression selector)
    {
        var body = selector.Body;

        // The compiler may wrap the member access in a Convert when the selector's declared return
        // type is a base/interface of the property type (e.g. List<T> property, IEnumerable<T> selector).
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            body = unary.Operand;

        if (body is MemberExpression member && member.Expression == selector.Parameters[0])
            return member.Member;

        throw new ArgumentException("The member selector must be a simple property access, e.g. d => d.Name.", nameof(selector));
    }

    private sealed class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression from;
        private readonly Expression to;

        public ParameterReplacer(ParameterExpression from, Expression to)
        {
            this.from = from;
            this.to = to;
        }

        protected override Expression VisitParameter(ParameterExpression node)
            => node == this.from ? this.to : base.VisitParameter(node);
    }
}
