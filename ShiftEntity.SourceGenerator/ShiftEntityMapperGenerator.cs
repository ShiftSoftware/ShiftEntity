using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace ShiftSoftware.ShiftEntity.SourceGenerator;

/// <summary>
/// Generates convention-based mappers and registers them (via module initializers) in
/// ShiftEntityMapperRegistry:
///
///  - TRIPLE mappers (IShiftEntityMapper&lt;TEntity, TListDTO, TViewDTO&gt;): auto-discovered from
///    ShiftRepository&lt;DB, TEntity, TList, TView&gt; subclasses and [ShiftEntityEndpoint*] attributes,
///    or programmer-declared [ShiftEntityMapper] partial classes (customization: user-implemented
///    methods win and can call the emitted *Generated bodies; per-property Configure hook).
///  - PAIR mappers (IShiftObjectMapper&lt;TChildEntity, TChildDto&gt;): auto-discovered from complex
///    members of view DTOs (different-class child pairs, single or collection), generated recursively
///    (grandchildren) with cycle detection, and COMPOSED automatically into the parents' MapToView
///    (child DTO), MapToEntity (replace-with-new via MapBack) and MapToList (inline SQL member-init),
///    each up to MaxDepth. The three directions compose the SAME set of members — a child the view
///    reads must be a child the entity writes, or an upsert silently empties it. Declared
///    [ShiftEntityMapper] partial pair classes replace the auto ones; ForEntityChildren /
///    ForListChildren remain the explicit sugar, and still compose past the cap.
///
/// Conventions: scalars by name + implicit conversion; entity T? → DTO T narrowing (?? default);
/// long/long? → string and enum → int(?); FK ↔ ShiftEntitySelectDTO via MappingHelpers; string ↔
/// List&lt;ShiftFileDTO&gt;; view base fields via MapBaseFields; CopyEntity = generated property-by-property
/// copy. MapToList is an inline SQL-translatable projection (SelectWithTags for taggables).
///
/// Diagnostics: SHENGEN001 (not partial), SHENGEN002 (no mapper interface), SHENGEN003 (deep-mapping
/// cycle — member skipped), SHENGEN004 (unmapped view members — warning), SHENGEN005 (conditional mapper
/// configuration), SHENGEN006 (entity repository configuration suppressed by a repository's builder).
/// The errors (001/002/005/006) all mark something that cannot be expressed at build time or would run
/// silently wrong; the warnings (003/004) mark something merely skipped.
///
/// NOTE: emission is centralized in one combined step (pair closure needs global knowledge), so
/// incremental caching granularity is coarse — any relevant change regenerates all mappers. Correctness
/// over incrementality; acceptable at framework-consumer scale.
/// </summary>
[Generator]
public sealed class ShiftEntityMapperGenerator : IIncrementalGenerator
{
    private const string AttributeFullName = "ShiftSoftware.ShiftEntity.Core.ShiftEntityMapperAttribute";
    private const string Helpers = "global::ShiftSoftware.ShiftEntity.Core.MappingHelpers";
    // Core, not EFCore: the generator ships inside ShiftEntity.Core, so emitting a call into ShiftEntity.EFCore
    // meant a Core-only project with a taggable entity got source that did not compile. An [Obsolete] forwarder
    // stays in EFCore for one release, because mappers baked into already-published packages call the old name.
    private const string TaggableExtensions = "global::ShiftSoftware.ShiftEntity.Core.Tagging.TaggableProjectionExtensions";
    private const string AutoNamespace = "ShiftSoftware.ShiftEntity.GeneratedMappers";
    private const int DefaultMaxDepth = 10;   // mirror of ShiftEntityMapperDefaults.MaxDepth

    private static readonly DiagnosticDescriptor NotPartial = new(
        "SHENGEN001", "Mapper class must be partial",
        "Class '{0}' is marked [ShiftEntityMapper] but is not declared partial; the generator cannot add the mapping methods",
        "ShiftEntity.Mapping", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor NoMapperInterface = new(
        "SHENGEN002", "Mapper class must implement a mapper interface",
        "Class '{0}' is marked [ShiftEntityMapper] but implements neither IShiftEntityMapper<TEntity, TListDTO, TViewDTO> nor IShiftObjectMapper<TEntity, TDto>",
        "ShiftEntity.Mapping", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor MappingCycle = new(
        "SHENGEN003", "Deep mapping cycle",
        "Deep-mapping cycle detected ({0}); the member '{1}' is skipped — customize it via ForView/Configure or remove it from the DTO",
        "ShiftEntity.Mapping", DiagnosticSeverity.Warning, true);

    private static readonly DiagnosticDescriptor UnmappedMembers = new(
        "SHENGEN004", "Unmapped members",
        "Generated mapper '{0}' does not map: {1} — no convention or deep composition applies; customize via ForView/Configure, take the method over, or adjust the DTO",
        "ShiftEntity.Mapping", DiagnosticSeverity.Warning, true);

    private static readonly DiagnosticDescriptor ConditionalConfig = new(
        "SHENGEN005", "Conditional mapper configuration",
        "Mapper configuration '{0}' is registered conditionally (inside an if/else/switch/loop/?: or &&/||/??). The generator bakes customization decisions at build time, so a skipped branch would silently drop the member to its default. Register it UNCONDITIONALLY and put the condition INSIDE the value delegate instead — e.g. map.ForView(d => d.X, (e, _) => cond ? a : b).",
        "ShiftEntity.Mapping", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor UnmappedListMembers = new(
        "SHENGEN007", "Unmapped list members",
        "Generated mapper '{0}' does not project into the list: {1} — the column comes back empty. Add the mapping to the repository's UseGeneratedMapper(map => …) or the mapper's Configure, or IgnoreList it if the column is not wanted",
        "ShiftEntity.Mapping", DiagnosticSeverity.Warning, true);

    private static readonly DiagnosticDescriptor DeepWriteReplacesTrackedChildren = new(
        "SHENGEN010", "Deep write replaces tracked child rows",
        "Generated mapper '{0}' composes '{1}' automatically on the write side, but {2} is a tracked entity with a required foreign key back to {3}. Automatic deep write is REPLACE-WITH-NEW, so saving will either fail on the foreign key or orphan and duplicate rows. Reconcile them instead with map.AfterEntity(...) or an existing-aware map.ForEntity(...), or IgnoreEntity the member and handle it in the repository",
        "ShiftEntity.Mapping", DiagnosticSeverity.Warning, true);

    private static readonly DiagnosticDescriptor UnbakeableConfig = new(
        "SHENGEN009", "Mapper configuration cannot be baked",
        "Mapper configuration '{0}' cannot be read at build time ({1}), so the generator would bake the plain convention and the call would do nothing at all. Use a plain property selector (d => d.Name), a constant depth, and a closed generic receiver",
        "ShiftEntity.Mapping", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor EntityWriteAsymmetry = new(
        "SHENGEN008", "View members that are never written back",
        "Generated mapper '{0}' reads these DTO members in MapToView but never writes them back in MapToEntity: {1} — they will display correctly and silently fail to save. Map them with ForEntity, or mark them read-only with IgnoreEntity",
        "ShiftEntity.Mapping", DiagnosticSeverity.Warning, true);

    private static readonly DiagnosticDescriptor EntityConfigSuppressed = new(
        "SHENGEN006", "Entity repository configuration will not run",
        "'{0}' declares IConfiguresShiftRepository<{0}, {1}, {2}>, but repository '{3}' configures the same triple by passing an options builder to its base constructor. Passing a builder means the repository configures itself and takes over completely, so '{0}'.ConfigureRepository will NOT run — move that configuration into '{3}'s builder, or drop the builder to let the entity's configuration apply.",
        "ShiftEntity.Repository", DiagnosticSeverity.Error, true);

    // ─────────────────────────────────── pipeline ───────────────────────────────────

    private sealed record DeclaredModel(INamedTypeSymbol Cls, string? Error, bool IsPair,
        ITypeSymbol? Entity, ITypeSymbol? ListDto, ITypeSymbol? ViewDto);

    private sealed record TripleModel(ITypeSymbol Entity, ITypeSymbol ListDto, ITypeSymbol ViewDto, int? MaxDepth = null,
        // RepoConfiguresItself / RepoName are for SHENGEN006 only: whether the repository passes an options
        // builder to ShiftRepository's constructor, and what it is called.
        //
        // RepoLocation is where every diagnostic about this triple points. For the dominant shape — an
        // UseGeneratedMapper() triple with no [ShiftEntityMapper] partial — there is no user class to point at,
        // so without this the warning has no file and no line: it cannot be double-clicked and cannot be
        // suppressed locally. It is the repository declaration, or the endpoint attribute that declared the triple.
        bool RepoConfiguresItself = false, string? RepoName = null, Location? RepoLocation = null);

    private sealed record PairSeed(ITypeSymbol Entity, ITypeSymbol Dto);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var declared = context.SyntaxProvider.ForAttributeWithMetadataName(
                AttributeFullName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => BuildDeclared(ctx))
            .Collect();

        var repoTriples = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, _) => BuildFromRepository(ctx))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            .Collect();

        var endpointTriples = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                static (ctx, _) => BuildFromEndpointAttributes(ctx))
            .SelectMany(static (models, _) => models)
            .Collect();

        // Pairs opted-in by a deep-mapping builder CALL — ForListChild(ren)/ForEntityChild(ren). The call
        // site itself is the opt-in and already carries both the (child entity, child DTO) types and the
        // direction, so simple cases need no [ShiftEntityMapper] partial and no attribute, and no method
        // signature changes. Discovered here, generated + registered exactly like a declared pair.
        var configPairs = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax mae } &&
                    IsDeepMappingMethod(mae.Name.Identifier.ValueText),
                static (ctx, _) => BuildConfigPair(ctx))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            .Collect();

        // Per-property configuration read STATICALLY from the fluent tree — ForView/ForEntity/ForList/ForCopy
        // (+ the ForXxxChild(ren) explicit-deep calls) mark a member customized; Ignore(View/Entity/List/Copy)
        // marks it excluded; MaxDepth(n) sets the cap. Discovered wherever they appear (a mapper's Configure,
        // a repository's UseGeneratedMapper(map => …), a nested configureChild) because the receiver's generic
        // arguments identify the (entity, list, view) triple. The generator BAKES the decision — no runtime branch.
        var configCalls = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax mae } &&
                    IsConfigMethod(mae.Name.Identifier.ValueText),
                static (ctx, _) => BuildConfigCall(ctx))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            .Collect();

        var everything = declared.Combine(repoTriples).Combine(endpointTriples).Combine(configPairs)
            .Combine(configCalls).Combine(context.CompilationProvider);

        context.RegisterSourceOutput(everything, static (spc, data) =>
        {
            var (((((declaredModels, fromRepos), fromEndpoints), configSeeds), configuration), compilation) = data;
            GenerateAll(spc, declaredModels, fromRepos, fromEndpoints, configSeeds, configuration, compilation);
        });
    }

    // NOTE: there is deliberately no ForCopyChild(ren). CopyEntity is a shallow, same-type copy, so a deep
    // child variant would have no meaning. Per-member copy customization is ForCopy / IgnoreCopy /
    // [ShiftEntityMapperIgnore]. Both names used to be listed here and documented, and neither ever existed.
    private static bool IsDeepMappingMethod(string name) =>
        name is "ForListChildren" or "ForListChild" or "ForEntityChildren" or "ForEntityChild"
             or "ForViewChildren" or "ForViewChild"
             // Direction-scoped child builders expose the deep methods without a direction in the name;
             // the direction comes from the receiver type (ShiftXxxChildMapper) instead.
             or "ForChild" or "ForChildren";

    // A deep-mapping / config call receiver is either the root ShiftMapperBuilder<E,L,V> or one of the
    // direction-scoped child builders ShiftXxxChildMapper<TChildEntity, TChildDto>.
    private static bool IsBuilderOrChildMapper(INamedTypeSymbol type) =>
        type.Name is "ShiftMapperBuilder" or "ShiftViewChildMapper" or "ShiftListChildMapper" or "ShiftEntityChildMapper";

    // Direction implied by a direction-scoped child-mapper receiver type. null → not a child-mapper type.
    private static MapDir? ChildMapperDirection(string typeName) => typeName switch
    {
        "ShiftViewChildMapper" => MapDir.View,
        "ShiftListChildMapper" => MapDir.List,
        "ShiftEntityChildMapper" => MapDir.Entity,
        _ => (MapDir?)null,
    };

    // Every deep-mapping builder method is <TChildEntity, TChildDto>(...) in that order — read the pair
    // straight off the (inference-resolved) call symbol. Guards keep it to genuine ShiftMapperBuilder calls.
    private static PairSeed? BuildConfigPair(GeneratorSyntaxContext ctx)
    {
        if (ctx.SemanticModel.GetSymbolInfo((InvocationExpressionSyntax)ctx.Node).Symbol is not IMethodSymbol method ||
            !IsDeepMappingMethod(method.Name) ||
            method.TypeArguments.Length != 2 ||
            method.ContainingType is not { } container ||
            !IsBuilderOrChildMapper(container) ||
            !IsShiftNamespace(container))
            return null;

        var entity = method.TypeArguments[0];
        var dto = method.TypeArguments[1];

        return IsOpenOrError(entity) || IsOpenOrError(dto) ? null : new PairSeed(entity, dto);
    }

    // ─────────────────────────────────── build-time config scan ───────────────────────────────────

    private enum MapDir { View, Entity, List, Copy, All }
    private enum MapKind { Custom, Ignore, MaxDepth }

    // One statically-read fluent config call: which triple it targets, what it does, to which member.
    // Conditional = the call sits inside a branch (if/switch/loop/?:/&&/||/??) within its config body — the
    // generator bakes decisions, so conditional registration is an error (SHENGEN005).
    /// <summary>
    /// One fluent configuration call the generator found.
    /// <para>
    /// <paramref name="UnbakeableReason"/> is set when the call was recognised as configuration but could not be
    /// READ — an open-generic receiver, a selector that is not a plain property access, a computed depth. Such a
    /// call used to be dropped silently, and dropping it means the generated code bakes the plain convention: the
    /// registration compiles, runs, and has no effect whatsoever. It is reported instead.
    /// </para>
    /// </summary>
    private sealed record ConfigCall(string TripleKey, MapKind Kind, MapDir Dir, string? Member, int Depth,
        bool Conditional, Location Location, string MethodName, string? UnbakeableReason = null);

    /// <summary>A call that is configuration but cannot be baked — carried so it can be reported, not dropped.</summary>
    private static ConfigCall Unbakeable(Location location, string methodName, string reason) =>
        new("", MapKind.Custom, MapDir.All, null, 0, false, location, methodName, reason);

    private static bool IsConfigMethod(string name) =>
        name is "ForView" or "ForEntity" or "ForList" or "ForCopy"
             or "ForViewChild" or "ForViewChildren" or "ForEntityChild" or "ForEntityChildren"
             or "ForListChild" or "ForListChildren"
             or "Ignore" or "IgnoreView" or "IgnoreEntity" or "IgnoreList" or "IgnoreCopy"
             or "MaxDepth"
             // Direction-scoped child-builder surface (direction comes from the receiver type).
             or "For" or "ForChild" or "ForChildren";

    private static ConfigCall? BuildConfigCall(GeneratorSyntaxContext ctx)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;

        if (ctx.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method ||
            method.ContainingType is not { } container ||
            !IsShiftNamespace(container))
            return null;

        var name = method.Name;
        var conditional = IsInConditionalContext(invocation);
        var location = invocation.GetLocation();

        // The framework's own builders forward between overloads and out of ShiftChildMapperBuilder, so their
        // receivers are open generic by construction. Those are API DEFINITIONS, not registrations — the real
        // registration is the user's call into them, with concrete types, which is handled below. Reporting them
        // would break the framework's own build, and SHENGEN diagnostics only fire on a certainty.
        var enclosing = ctx.SemanticModel.GetEnclosingSymbol(invocation.SpanStart)?.ContainingType;
        var inFrameworkPlumbing = enclosing is not null && IsShiftNamespace(enclosing);

        // Nested child config: receiver is a direction-scoped ShiftXxxChildMapper<TChildEntity, TChildDto>.
        // The pair mapper's baked directives are keyed by its (entity, dto, dto) triple; the direction comes
        // from the receiver TYPE (For/Ignore/ForChild/ForChildren carry no direction in the name). Its member
        // selector is over the child DTO (view/list) or the child entity (entity direction) — MemberNameFromSelector
        // reads the name either way.
        if (ChildMapperDirection(container.Name) is { } childDir)
        {
            if (container.TypeArguments.Length != 2)
                return null;

            var childEntity = container.TypeArguments[0];
            var childDto = container.TypeArguments[1];

            if (IsOpenOrError(childEntity) || IsOpenOrError(childDto))
                return inFrameworkPlumbing
                    ? null
                    : Unbakeable(location, name, "the child mapper's type arguments are not closed generic types");

            var childMember = MemberNameFromSelector(invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression);
            if (childMember is null)
                return inFrameworkPlumbing
                    ? null
                    : Unbakeable(location, name, "the member selector is not a plain property access");

            var childKind = name == "Ignore" ? MapKind.Ignore : MapKind.Custom;
            return new ConfigCall(TripleKey(childEntity, childDto, childDto), childKind, childDir, childMember, 0, conditional, location, name);
        }

        if (container.Name != "ShiftMapperBuilder" || container.TypeArguments.Length != 3)
            return null;

        var e = container.TypeArguments[0];
        var l = container.TypeArguments[1];
        var v = container.TypeArguments[2];

        if (IsOpenOrError(e) || IsOpenOrError(l) || IsOpenOrError(v))
            return inFrameworkPlumbing
                ? null
                : Unbakeable(location, name, "the mapper builder's type arguments are not closed generic types");

        var key = TripleKey(e, l, v);

        // map.MaxDepth(constant) — read the CONSTANT depth at build time (a computed value can't be baked).
        if (name == "MaxDepth")
        {
            var arg = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            if (arg is not null && ctx.SemanticModel.GetConstantValue(arg) is { HasValue: true, Value: int depth })
                return new ConfigCall(key, MapKind.MaxDepth, MapDir.All, null, depth, conditional, location, name);

            return inFrameworkPlumbing
                ? null
                : Unbakeable(location, name, "the depth is not a compile-time constant");
        }

        var member = MemberNameFromSelector(invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression);
        if (member is null)
            return inFrameworkPlumbing
                ? null
                : Unbakeable(location, name, "the member selector is not a plain property access");

        var dir =
            name.Contains("View") ? MapDir.View :
            name.Contains("Entity") ? MapDir.Entity :
            name.Contains("List") ? MapDir.List :
            name.Contains("Copy") ? MapDir.Copy :
            MapDir.All;   // bare Ignore

        var kind = name.StartsWith("Ignore", StringComparison.Ordinal) ? MapKind.Ignore : MapKind.Custom;

        return new ConfigCall(key, kind, dir, member, 0, conditional, location, name);
    }

    // True if the fluent config call is registered conditionally — i.e. some branch node sits between it and
    // its enclosing config body (the Configure method or a UseGeneratedMapper / configureChild lambda). The
    // value delegate is a DESCENDANT of the call, so a condition INSIDE the value (map.ForView(x, e => c ? a : b))
    // is correctly NOT flagged — only a condition AROUND the registration is.
    private static bool IsInConditionalContext(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                // Reached the config body boundary without crossing a branch → unconditional.
                case SimpleLambdaExpressionSyntax:
                case ParenthesizedLambdaExpressionSyntax:
                case AnonymousMethodExpressionSyntax:
                case MethodDeclarationSyntax:
                case LocalFunctionStatementSyntax:
                    return false;

                case IfStatementSyntax:
                case ElseClauseSyntax:
                case ConditionalExpressionSyntax:
                case SwitchStatementSyntax:
                case SwitchExpressionSyntax:
                case SwitchExpressionArmSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case CatchClauseSyntax:
                    return true;

                case BinaryExpressionSyntax bin when bin.IsKind(SyntaxKind.LogicalAndExpression) ||
                                                     bin.IsKind(SyntaxKind.LogicalOrExpression) ||
                                                     bin.IsKind(SyntaxKind.CoalesceExpression):
                    return true;
            }
        }

        return false;
    }

    // The member name from a `d => d.X` selector (unwraps a Convert the compiler may add).
    private static string? MemberNameFromSelector(ExpressionSyntax? arg)
    {
        var body = arg switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Body,
            ParenthesizedLambdaExpressionSyntax paren => paren.Body,
            _ => null,
        };

        while (body is CastExpressionSyntax cast)
            body = cast.Expression;

        return body is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var text } ? text : null;
    }

    private static DeclaredModel BuildDeclared(GeneratorAttributeSyntaxContext ctx)
    {
        var cls = (INamedTypeSymbol)ctx.TargetSymbol;

        var isPartial = cls.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<ClassDeclarationSyntax>()
            .Any(s => s.Modifiers.Any(SyntaxKind.PartialKeyword));

        if (!isPartial)
            return new DeclaredModel(cls, "partial", false, null, null, null);

        var tripleInterface = cls.AllInterfaces.FirstOrDefault(i =>
            i.Name == "IShiftEntityMapper" && i.TypeArguments.Length == 3 && IsShiftNamespace(i));

        if (tripleInterface is not null)
            return new DeclaredModel(cls, null, false,
                tripleInterface.TypeArguments[0], tripleInterface.TypeArguments[1], tripleInterface.TypeArguments[2]);

        var pairInterface = cls.AllInterfaces.FirstOrDefault(i =>
            i.Name == "IShiftObjectMapper" && i.TypeArguments.Length == 2 && IsShiftNamespace(i));

        if (pairInterface is not null)
            return new DeclaredModel(cls, null, true,
                pairInterface.TypeArguments[0], null, pairInterface.TypeArguments[1]);

        return new DeclaredModel(cls, "interface", false, null, null, null);
    }

    private static TripleModel? BuildFromRepository(GeneratorSyntaxContext ctx)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node) is not INamedTypeSymbol cls)
            return null;

        for (var current = cls.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name != "ShiftRepository" || current.TypeArguments.Length != 4 || !IsShiftNamespace(current))
                continue;

            var entity = current.TypeArguments[1];
            var listDto = current.TypeArguments[2];
            var viewDto = current.TypeArguments[3];

            if (IsOpenOrError(entity) || IsOpenOrError(listDto) || IsOpenOrError(viewDto))
                return null;

            // Per-repository knob (the intended home for MaxDepth): read off the repo class.
            return new TripleModel(entity, listDto, viewDto, ReadMaxDepthAttr(cls),
                PassesOptionsBuilder(cls), cls.Name, cls.Locations.FirstOrDefault());
        }

        return null;
    }

    /// <summary>
    /// Whether a repository configures ITSELF — i.e. hands an options builder to
    /// <c>ShiftRepository(DB db, Action&lt;ShiftRepositoryOptions&lt;…&gt;&gt;? shiftRepositoryBuilder = null)</c>.
    /// That takes over configuration completely, so the entity's <c>IConfiguresShiftRepository</c> won't run
    /// (SHENGEN006 fails the build on the pairing).
    /// <para>
    /// Answered only for a class whose DIRECT base is <c>ShiftRepository&lt;,,,&gt;</c>, so we read the very
    /// <c>base(...)</c> call that reaches it. Through an intermediate class (e.g. a project's own
    /// <c>RepositoryBase</c> forwarding a builder parameter) whether a builder actually arrives is a runtime
    /// value, so we return false and stay silent rather than break a build on a guess.
    /// </para>
    /// <para>
    /// EVERY constructor reaching base must pass a builder. One that doesn't means the repository can also be
    /// built the entity-configured way, so the suppression is a maybe — and SHENGEN006 breaks the build, so it
    /// may only fire on a certainty. Constructors chaining through <c>: this(...)</c> aren't base calls; the one
    /// they land on is visited on its own turn.
    /// </para>
    /// </summary>
    private static bool PassesOptionsBuilder(INamedTypeSymbol cls)
    {
        if (cls.BaseType is not INamedTypeSymbol baseType
            || baseType.Name != "ShiftRepository" || baseType.TypeArguments.Length != 4 || !IsShiftNamespace(baseType))
            return false;

        var reachesBase = false;

        foreach (var ctor in cls.InstanceConstructors)
        {
            foreach (var syntaxRef in ctor.DeclaringSyntaxReferences)
            {
                if (syntaxRef.GetSyntax() is not ConstructorDeclarationSyntax decl)
                    continue;

                if (decl.Initializer is { } chain && chain.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword))
                    continue;

                reachesBase = true;

                // base(db) => no builder. base(db, x => …) / base(db, SomeOptions) => configures itself.
                // An explicit base(db, null) is "no builder", so don't count it.
                if (decl.Initializer is not { } init
                    || init.ArgumentList.Arguments.Count < 2
                    || init.ArgumentList.Arguments[1].Expression.IsKind(SyntaxKind.NullLiteralExpression))
                    return false;
            }
        }

        return reachesBase;
    }

    /// <summary>Whether the entity declares <c>IConfiguresShiftRepository</c> for this exact triple.</summary>
    private static bool EntityConfiguresTriple(ITypeSymbol entity, ITypeSymbol listDto, ITypeSymbol viewDto) =>
        entity.AllInterfaces.Any(i =>
            i.Name == "IConfiguresShiftRepository" && i.TypeArguments.Length == 3 && IsShiftNamespace(i)
            && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], entity)
            && SymbolEqualityComparer.Default.Equals(i.TypeArguments[1], listDto)
            && SymbolEqualityComparer.Default.Equals(i.TypeArguments[2], viewDto));

    private static int? ReadMaxDepthAttr(ISymbol symbol) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "ShiftEntityMapperMaxDepthAttribute" &&
            a.AttributeClass is { } c && IsShiftNamespace(c)) is { ConstructorArguments.Length: > 0 } attr &&
            attr.ConstructorArguments[0].Value is int depth ? depth : null;

    private static bool HasIgnoreAttr(ISymbol symbol) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.Name == "ShiftEntityMapperIgnoreAttribute" &&
            a.AttributeClass is { } c && IsShiftNamespace(c));

    // Member names carrying [ShiftEntityMapperIgnore] on ANY side (entity / view DTO / list DTO). A single
    // attribute excludes the member in every direction, matching the all-directions fluent Ignore.
    private static HashSet<string> CollectAttrIgnored(params ITypeSymbol[] types)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in types)
            foreach (var prop in AllProps(type))
                if (HasIgnoreAttr(prop))
                    set.Add(prop.Name);

        return set;
    }

    private static ImmutableArray<TripleModel> BuildFromEndpointAttributes(GeneratorSyntaxContext ctx)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node) is not INamedTypeSymbol entity ||
            !DerivesFrom(entity, "ShiftEntity"))
            return ImmutableArray<TripleModel>.Empty;

        var models = ImmutableArray.CreateBuilder<TripleModel>();

        foreach (var attribute in entity.GetAttributes())
        {
            var attrClass = attribute.AttributeClass;

            if (attrClass is null ||
                !attrClass.Name.StartsWith("ShiftEntity", StringComparison.Ordinal) ||
                !attrClass.Name.Contains("Endpoint") ||
                attrClass.TypeArguments.Length < 2 ||
                !IsShiftNamespace(attrClass))
                continue;

            var listDto = attrClass.TypeArguments[0];
            var viewDto = attrClass.TypeArguments[1];

            if (!IsOpenOrError(listDto) && !IsOpenOrError(viewDto))
                // Point at the attribute application, not the entity: an entity can declare several endpoints,
                // and the one that declared THIS triple is the useful place to land.
                models.Add(new TripleModel(entity, listDto, viewDto,
                    RepoLocation: attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                                  ?? entity.Locations.FirstOrDefault()));
        }

        return models.ToImmutable();
    }

    private static bool IsOpenOrError(ITypeSymbol t) => t is ITypeParameterSymbol || t.TypeKind == TypeKind.Error;

    // ─────────────────────────────────── the combined generation step ───────────────────────────────────

    // The baked per-member decisions for one triple/pair, aggregated from every fluent config call that
    // targets it. A member in a direction's Custom set → emit a reference to the runtime delegate (no branch);
    // in the Ignore set → omit the member; otherwise → the generated convention / auto deep-composition.
    private sealed class MapperDirectives
    {
        public readonly HashSet<string> ViewCustom = new(StringComparer.Ordinal);
        public readonly HashSet<string> EntityCustom = new(StringComparer.Ordinal);
        public readonly HashSet<string> ListCustom = new(StringComparer.Ordinal);
        public readonly HashSet<string> CopyCustom = new(StringComparer.Ordinal);
        public readonly HashSet<string> ViewIgnore = new(StringComparer.Ordinal);
        public readonly HashSet<string> EntityIgnore = new(StringComparer.Ordinal);
        public readonly HashSet<string> ListIgnore = new(StringComparer.Ordinal);
        public readonly HashSet<string> CopyIgnore = new(StringComparer.Ordinal);
        public int? MaxDepth;

        public static readonly MapperDirectives Empty = new();

        public bool IsCustom(MapDir d, string member) => CustomSet(d).Contains(member);
        public bool IsIgnored(MapDir d, string member) => IgnoreSet(d).Contains(member);

        public HashSet<string> CustomSet(MapDir d) => d switch
        {
            MapDir.Entity => EntityCustom,
            MapDir.List => ListCustom,
            MapDir.Copy => CopyCustom,
            _ => ViewCustom,
        };

        public HashSet<string> IgnoreSet(MapDir d) => d switch
        {
            MapDir.Entity => EntityIgnore,
            MapDir.List => ListIgnore,
            MapDir.Copy => CopyIgnore,
            _ => ViewIgnore,
        };

        public void AddCustom(MapDir d, string member)
        {
            if (d == MapDir.All) { ViewCustom.Add(member); EntityCustom.Add(member); ListCustom.Add(member); CopyCustom.Add(member); }
            else CustomSet(d).Add(member);
        }

        public void AddIgnore(MapDir d, string member)
        {
            if (d == MapDir.All) { ViewIgnore.Add(member); EntityIgnore.Add(member); ListIgnore.Add(member); CopyIgnore.Add(member); }
            else IgnoreSet(d).Add(member);
        }
    }

    private static Dictionary<string, MapperDirectives> BuildDirectives(ImmutableArray<ConfigCall> calls)
    {
        var map = new Dictionary<string, MapperDirectives>(StringComparer.Ordinal);

        foreach (var call in calls)
        {
            // A call the generator could not read carries no member and no triple. It is reported as SHENGEN009
            // rather than baked — recording it here would mean a null member name in the directive sets.
            if (call.UnbakeableReason is not null)
                continue;

            if (!map.TryGetValue(call.TripleKey, out var d))
                map[call.TripleKey] = d = new MapperDirectives();

            switch (call.Kind)
            {
                case MapKind.MaxDepth: d.MaxDepth = call.Depth; break;
                case MapKind.Custom: d.AddCustom(call.Dir, call.Member!); break;
                case MapKind.Ignore: d.AddIgnore(call.Dir, call.Member!); break;
            }
        }

        return map;
    }

    private sealed class PairInfo
    {
        public ITypeSymbol Entity = null!;
        public ITypeSymbol Dto = null!;
        public INamedTypeSymbol? UserClass;
        public string ClassName = "";   // simple name (auto) — for declared pairs the user class name
        public string TypeRef = "";     // fully qualified reference used by consumers

        /// <summary>
        /// Where the triple that pulled this pair in was declared. An auto pair has no user class, so this is
        /// the only navigable place a diagnostic about it can point.
        /// </summary>
        public Location? OriginLocation;
    }

    private static void GenerateAll(SourceProductionContext spc,
        ImmutableArray<DeclaredModel> declaredModels,
        ImmutableArray<TripleModel> fromRepos,
        ImmutableArray<TripleModel> fromEndpoints,
        ImmutableArray<PairSeed> configSeeds,
        ImmutableArray<ConfigCall> configuration,
        Compilation compilation)
    {
        // Per-member baked decisions read from the fluent config, plus the compilation-wide depth default.
        var directives = BuildDirectives(configuration);
        var assemblyMaxDepth = ReadMaxDepthAttr(compilation.Assembly) ?? DefaultMaxDepth;

        // Conditional registration can't be baked (a skipped branch would silently drop the member) — error out.
        foreach (var call in configuration)
            if (call.Conditional && call.UnbakeableReason is null)
                spc.ReportDiagnostic(Diagnostic.Create(ConditionalConfig, call.Location,
                    call.Member is null ? call.MethodName : $"{call.MethodName}({call.Member})"));

        // Same stance, wider: a registration the generator recognised but could not READ. Silently ignoring it
        // bakes the plain convention, so the call compiles, runs, and does nothing — which is how `Ignore` came
        // to be decoration rather than instruction.
        foreach (var call in configuration)
            if (call.UnbakeableReason is not null)
                spc.ReportDiagnostic(Diagnostic.Create(UnbakeableConfig, call.Location,
                    call.MethodName, call.UnbakeableReason));

        // A repository that passes an options builder configures itself and takes over, so an entity declaring
        // IConfiguresShiftRepository for the SAME triple has its ConfigureRepository silently skipped. Both halves
        // look correct in isolation and nothing fails at runtime — the config just never runs — so break the build,
        // where the programmer can see both files. An error rather than a warning for the same reason as SHENGEN005:
        // the failure is invisible at runtime, and the fix (move the config into the builder, or drop the builder)
        // is always available. PassesOptionsBuilder only reports a certainty, so this can't fire on a guess.
        foreach (var triple in fromRepos)
            if (triple.RepoConfiguresItself && EntityConfiguresTriple(triple.Entity, triple.ListDto, triple.ViewDto))
                spc.ReportDiagnostic(Diagnostic.Create(EntityConfigSuppressed, triple.RepoLocation,
                    triple.Entity.Name, triple.ListDto.Name, triple.ViewDto.Name, triple.RepoName));

        // 1. Declared classes: report errors; index valid ones.
        var declaredTriples = new Dictionary<string, DeclaredModel>(StringComparer.Ordinal);
        var declaredPairs = new Dictionary<string, DeclaredModel>(StringComparer.Ordinal);

        foreach (var model in declaredModels)
        {
            if (model.Error is not null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    model.Error == "partial" ? NotPartial : NoMapperInterface,
                    model.Cls.Locations.FirstOrDefault() ?? Location.None, model.Cls.Name));
                continue;
            }

            if (model.IsPair)
                declaredPairs[PairKey(model.Entity!, model.ViewDto!)] = model;
            else
                declaredTriples[TripleKey(model.Entity!, model.ListDto!, model.ViewDto!)] = model;
        }

        // 2. All triples (declared + auto, deduped; declared wins its key).
        var triples = new Dictionary<string, (TripleModel Triple, INamedTypeSymbol? UserClass)>(StringComparer.Ordinal);

        foreach (var model in declaredTriples.Values)
            triples[TripleKey(model.Entity!, model.ListDto!, model.ViewDto!)] =
                (new TripleModel(model.Entity!, model.ListDto!, model.ViewDto!), model.Cls);

        foreach (var triple in fromRepos.Concat(fromEndpoints))
            if (!triples.ContainsKey(TripleKey(triple.Entity, triple.ListDto, triple.ViewDto)))
                triples[TripleKey(triple.Entity, triple.ListDto, triple.ViewDto)] = (triple, null);

        // 3. Pair closure over all view DTOs, with cycle detection (cycle edge → warn + skip).
        var pairs = new Dictionary<string, PairInfo>(StringComparer.Ordinal);
        var skippedEdges = new HashSet<string>(StringComparer.Ordinal);   // "<ownerKey>|<member>"
        var stack = new List<string>();

        void Discover(string ownerKey, string ownerDisplay, ITypeSymbol entity, ITypeSymbol viewDto, Location? origin)
        {
            var entityProps = AllProps(entity).Where(IsReadable).ToDictionary(p => p.Name, p => p);

            foreach (var dtoProp in AllProps(viewDto).Where(IsSettable))
            {
                if (IsViewHandled(dtoProp) ||
                    ViewConvention(entityProps, dtoProp, compilation) is not null ||
                    !TryGetComposableChild(entityProps, dtoProp, out var childEntity, out var childDto, out _))
                    continue;

                var key = PairKey(childEntity, childDto);

                if (stack.Contains(key))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(MappingCycle, origin ?? Location.None,
                        string.Join(" → ", stack.Concat(new[] { key }).Select(ShortPair)),
                        $"{ownerDisplay}.{dtoProp.Name}"));
                    skippedEdges.Add(ownerKey + "|" + dtoProp.Name);
                    continue;
                }

                if (pairs.ContainsKey(key))
                    continue;

                declaredPairs.TryGetValue(key, out var declaredPair);

                var info = new PairInfo { Entity = childEntity, Dto = childDto, UserClass = declaredPair?.Cls, OriginLocation = origin };

                if (declaredPair is not null)
                {
                    info.ClassName = declaredPair.Cls.Name;
                    info.TypeRef = Fq(declaredPair.Cls);
                }
                else
                {
                    info.ClassName = $"Generated_Pair_{childEntity.Name}_{childDto.Name}_{Fnv8(key)}";
                    info.TypeRef = $"global::{AutoNamespace}.{info.ClassName}";
                }

                pairs[key] = info;

                stack.Add(key);
                Discover(key, ShortPair(key), childEntity, childDto, origin);
                stack.RemoveAt(stack.Count - 1);
            }
        }

        foreach (var (key, (triple, _)) in triples.Select(kv => (kv.Key, kv.Value)))
        {
            stack.Clear();
            Discover(key, triple.Entity.Name, triple.Entity, triple.ViewDto, triple.RepoLocation);
        }

        // Declared pairs that no triple references still get generated (they may be used explicitly).
        foreach (var kv in declaredPairs)
        {
            if (pairs.ContainsKey(kv.Key))
                continue;

            var model = kv.Value;
            pairs[kv.Key] = new PairInfo
            {
                Entity = model.Entity!,
                Dto = model.ViewDto!,
                UserClass = model.Cls,
                ClassName = model.Cls.Name,
                TypeRef = Fq(model.Cls),
                OriginLocation = model.Cls.Locations.FirstOrDefault(),
            };

            stack.Clear();
            stack.Add(kv.Key);
            Discover(kv.Key, ShortPair(kv.Key), model.Entity!, model.ViewDto!, model.Cls.Locations.FirstOrDefault());
        }

        // Pairs opted-in by a ForListChild(ren)/ForEntityChild(ren) call site — the call is the opt-in, so
        // no partial/attribute is needed. Seeded like a declared pair: generate, register, and recurse for
        // grandchildren. (A pair a triple/view already covers is skipped by the ContainsKey guard.)
        foreach (var seed in configSeeds)
        {
            var key = PairKey(seed.Entity, seed.Dto);

            if (pairs.ContainsKey(key))
                continue;

            declaredPairs.TryGetValue(key, out var declaredForSeed);

            var info = new PairInfo
            {
                Entity = seed.Entity,
                Dto = seed.Dto,
                UserClass = declaredForSeed?.Cls,
                OriginLocation = declaredForSeed?.Cls.Locations.FirstOrDefault(),
            };

            if (declaredForSeed is not null)
            {
                info.ClassName = declaredForSeed.Cls.Name;
                info.TypeRef = Fq(declaredForSeed.Cls);
            }
            else
            {
                info.ClassName = $"Generated_Pair_{seed.Entity.Name}_{seed.Dto.Name}_{Fnv8(key)}";
                info.TypeRef = $"global::{AutoNamespace}.{info.ClassName}";
            }

            pairs[key] = info;

            stack.Clear();
            stack.Add(key);
            Discover(key, ShortPair(key), seed.Entity, seed.Dto, info.OriginLocation);
        }

        // Resolves the effective config + max depth for a mapper keyed by its (entity,list,view) triple.
        // Max depth comes ONLY from [ShiftEntityMapperMaxDepth] (repo/mapper class → entity → assembly default).
        MapperDirectives Dir(string key) => directives.TryGetValue(key, out var d) ? d : MapperDirectives.Empty;
        // Fluent map.MaxDepth(n) (in directives) wins; else the [ShiftEntityMapperMaxDepth] attribute; else default.
        int MaxDepthFor(string key, int? repoOrClass) =>
            (directives.TryGetValue(key, out var d) ? d.MaxDepth : null) ?? repoOrClass ?? assemblyMaxDepth;

        // 3b. BFS from every root view DTO (depth 0 → children depth 1 …) carrying the ROOT's max depth, so each
        // pair records both its shortest composition depth AND the (most permissive) root cap that reaches it.
        // A pair reached shallowest at depth d auto-composes its own children (depth d+1) iff d+1 ≤ that root cap —
        // this is what makes View/Entity honour the per-repo cap at EVERY level (like List), at BUILD time.
        var rootsWithDepth = triples.Select(kv =>
            (kv.Value.Triple, MaxDepthFor(kv.Key, kv.Value.Triple.MaxDepth ?? ReadMaxDepthAttr(kv.Value.UserClass ?? (ISymbol)kv.Value.Triple.Entity))));
        var (minDepth, pairMaxDepth) = ComputeMinDepth(rootsWithDepth, pairs, compilation);

        // 4. Emit pair mappers.
        foreach (var (key, pair) in pairs.Select(kv => (kv.Key, kv.Value)))
        {
            var pairTripleKey = TripleKey(pair.Entity, pair.Dto, pair.Dto);
            var depth = minDepth.TryGetValue(key, out var md) ? md : 1;
            var cap = pairMaxDepth.TryGetValue(key, out var pm) ? pm : assemblyMaxDepth;
            EmitPair(spc, key, pair, pairs, skippedEdges, compilation, Dir(pairTripleKey), Dir, depth, cap);
        }

        // 5. Emit triple mappers.
        foreach (var (key, (triple, userClass)) in triples.Select(kv => (kv.Key, kv.Value)))
            EmitTriple(spc, key, triple, userClass, pairs, skippedEdges, compilation,
                Dir(key), Dir, MaxDepthFor(key, triple.MaxDepth ?? ReadMaxDepthAttr(userClass ?? (ISymbol)triple.Entity)));
    }

    // BFS shortest composition depth + effective root cap for every discovered pair (root children = depth 1).
    private static (Dictionary<string, int> MinDepth, Dictionary<string, int> PairMaxDepth) ComputeMinDepth(
        IEnumerable<(TripleModel Triple, int MaxDepth)> roots, Dictionary<string, PairInfo> pairs, Compilation compilation)
    {
        var minDepth = new Dictionary<string, int>(StringComparer.Ordinal);
        var pairMaxDepth = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<(ITypeSymbol Entity, ITypeSymbol Dto, int Depth, int RootMax)>();

        foreach (var (triple, max) in roots)
            queue.Enqueue((triple.Entity, triple.ViewDto, 0, max));

        while (queue.Count > 0)
        {
            var (entity, dto, depth, rootMax) = queue.Dequeue();
            var entityProps = AllProps(entity).Where(IsReadable).ToDictionary(p => p.Name, p => p);

            foreach (var dtoProp in AllProps(dto).Where(IsSettable))
            {
                if (IsViewHandled(dtoProp) ||
                    ViewConvention(entityProps, dtoProp, compilation) is not null ||
                    !TryGetComposableChild(entityProps, dtoProp, out var childEntity, out var childDto, out _))
                    continue;

                var key = PairKey(childEntity, childDto);
                var childDepth = depth + 1;

                // Most permissive root cap reaching this pair (a shared pair used by several roots composes as
                // deep as any of them allows; distinct-DTO endpoints — the usual case — have exactly one root).
                pairMaxDepth[key] = pairMaxDepth.TryGetValue(key, out var pm) ? System.Math.Max(pm, rootMax) : rootMax;

                if (minDepth.TryGetValue(key, out var existing) && existing <= childDepth)
                    continue;   // already reached at least this shallow — no improvement, prevents cycles looping

                minDepth[key] = childDepth;

                if (pairs.ContainsKey(key))
                    queue.Enqueue((childEntity, childDto, childDepth, rootMax));
            }
        }

        return (minDepth, pairMaxDepth);
    }

    private static string TripleKey(ITypeSymbol e, ITypeSymbol l, ITypeSymbol v) => $"{Fq(e)}|{Fq(l)}|{Fq(v)}";
    private static string PairKey(ITypeSymbol e, ITypeSymbol d) => $"{Fq(e)}~{Fq(d)}";
    private static string ShortPair(string pairKey)
    {
        var parts = pairKey.Split('~');
        return $"({parts[0].Split('.').Last()}, {parts[1].Split('.').Last()})";
    }

    // ─────────────────────────────────── emission: shared infra ───────────────────────────────────

    // The reserved-name sets below are matched ONLY against members the framework actually declares — see
    // IsFrameworkMember. A domain column that happens to be called Tags, Revisions or ID is not a framework
    // member and must map like any other property.
    private static readonly HashSet<string> ViewHandledMembers = new(StringComparer.Ordinal)
    { "ID", "IsDeleted", "CreateDate", "LastSaveDate", "CreatedByUserID", "LastSavedByUserID", "Tags", "Revisions" };

    // The audit and soft-delete columns are deliberately NOT here: the mapper maps them, exactly as AutoMapper's
    // unguarded ReverseMap did, and restricting who may change them is the repository's job (ShiftRepository's
    // upsert holds the soft-delete line) or an explicit map.IgnoreEntity(...). What remains is what the SAVE
    // PIPELINE owns exclusively — writing any of it from a request body is incoherent, not merely unsafe:
    //   "ID"                — the primary key. Writable only through a null-tolerant convention that does not yet
    //                         exist: EntityConvention resolves string? → long to ToLong(), which THROWS on the
    //                         null every insert carries, and deep write would push a child's key onto a fresh
    //                         entity (IDENTITY_INSERT). Its own change, not this one. Pinned by a test.
    //   "ReloadAfterSave"   — [NotMapped] request-scoped flag the repository arms before mapping and reads after
    //                         saving. A client value either cancels a needed refresh or buys unbounded round-trips.
    //   "AuditFieldsAreSet" — [NotMapped] stamping guard. Writing it short-circuits AuditStamper for the whole
    //                         unit of work, including the forced IsDeleted = false on insert, and leaves no trace.
    //   "IdempotencyKey"    — repository-owned, unique-indexed dedup key. A client-chosen value lets a caller
    //                         squat the key space and poison later idempotent creates.
    //   "Tags"              — owned by TaggingPipeline on both legs. Writing it composes Tag rows the pipeline
    //                         discards a line later, and "tags": null NREs on entity.Tags.Clear().
    private static readonly HashSet<string> EntityExcludedMembers = new(StringComparer.Ordinal)
    { "ID", "ReloadAfterSave", "AuditFieldsAreSet", "IdempotencyKey", "Tags" };

    private static readonly HashSet<string> CopyExcludedMembers = new(StringComparer.Ordinal)
    { "ID", "ReloadAfterSave", "AuditFieldsAreSet" };

    /// <summary>
    /// True when the property belongs to the FRAMEWORK rather than to the domain.
    /// <para>
    /// This is the gate that makes the reserved-name sets safe. Matching those names as bare strings meant a
    /// domain column called <c>Tags</c> or <c>Revisions</c> was silently dropped from the view, entity AND list
    /// directions — real data loss, not a theoretical one. It also made a pair mapper disagree with its own
    /// projection: a plain POCO's <c>ID</c> was skipped by <c>Map</c> and emitted by <c>Projection</c>.
    /// </para>
    /// <para>
    /// Two ways a member can be the framework's. Most are DECLARED on a framework base
    /// (<c>ShiftEntity&lt;&gt;</c>, <c>ShiftEntityViewAndUpsertDTO</c>, <c>ShiftEntityListDTO</c>) — walk any
    /// <c>override</c> back to its original definition first, since DTOs routinely override <c>ID</c>.
    /// <c>Tags</c> is the exception: it is declared on the entity/DTO itself, because that is how
    /// <c>IShiftEntityTaggable(DTO)</c> is satisfied, so it is resolved through the interface instead.
    /// </para>
    /// </summary>
    private static bool IsFrameworkMember(IPropertySymbol property)
    {
        var original = property;
        while (original.OverriddenProperty is not null)
            original = original.OverriddenProperty;

        if (original.ContainingType is { } declaring && IsShiftNamespace(declaring))
            return true;

        var owner = property.ContainingType;

        foreach (var contract in owner.AllInterfaces)
        {
            if (!IsShiftNamespace(contract))
                continue;

            foreach (var member in contract.GetMembers().OfType<IPropertySymbol>())
                if (SymbolEqualityComparer.Default.Equals(owner.FindImplementationForInterfaceMember(member), property))
                    return true;
        }

        return false;
    }

    /// <summary>A view-DTO member the framework fills in itself (MapBaseFields, the tagging pipeline).</summary>
    private static bool IsViewHandled(IPropertySymbol property) =>
        ViewHandledMembers.Contains(property.Name) && IsFrameworkMember(property);

    /// <summary>An entity member the SAVE pipeline owns — a mapper must never write it from client input.</summary>
    private static bool IsEntityExcluded(IPropertySymbol property) =>
        EntityExcludedMembers.Contains(property.Name) && IsFrameworkMember(property);

    private static bool IsCopyExcluded(IPropertySymbol property) =>
        CopyExcludedMembers.Contains(property.Name) && IsFrameworkMember(property);

    /// <summary>
    /// A list-DTO member the projection must leave alone: <c>Tags</c> is spliced in separately by the
    /// repository, and <c>Revisions</c> is loaded on demand. Deliberately NOT every framework member — <c>ID</c>
    /// and the audit fields are part of the list payload and have to keep projecting.
    /// </summary>
    private static bool IsFrameworkListMember(IPropertySymbol property) =>
        property.Name is "Tags" or "Revisions" && IsFrameworkMember(property);

    private static StringBuilder StartClass(string? ns, string className, string declaration, string builderType, string configurableInterface, string entityName, string listName, MapperDirectives directives)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated by ShiftSoftware.ShiftEntity.SourceGenerator — convention-based mapper implementation.");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("#pragma warning disable 0169, 0414");
        sb.AppendLine();

        if (ns is not null)
        {
            sb.Append("namespace ").Append(ns).AppendLine(";");
            sb.AppendLine();
        }

        sb.AppendLine(declaration);
        sb.AppendLine("{");

        sb.AppendLine($"    private {builderType} __shiftMapperBuilder;");
        sb.AppendLine("    private readonly object __shiftMapperLock = new object();");
        sb.AppendLine($"    private global::System.Linq.Expressions.Expression<global::System.Func<{entityName}, {listName}>> __shiftComposedListProjection;");
        sb.AppendLine();
        sb.AppendLine($"    private {builderType} __ShiftMap");
        sb.AppendLine("    {");
        sb.AppendLine("        get");
        sb.AppendLine("        {");
        sb.AppendLine("            // Double-checked locking: Configure runs exactly once and the fully-configured");
        sb.AppendLine("            // builder is published safely, so a mapper instance shared across threads");
        sb.AppendLine("            // (e.g. a DI singleton) never sees a partially-built or duplicated builder.");
        sb.AppendLine("            var builder = global::System.Threading.Volatile.Read(ref this.__shiftMapperBuilder);");
        sb.AppendLine("            if (builder == null)");
        sb.AppendLine("            {");
        sb.AppendLine("                lock (this.__shiftMapperLock)");
        sb.AppendLine("                {");
        sb.AppendLine("                    builder = this.__shiftMapperBuilder;");
        sb.AppendLine("                    if (builder == null)");
        sb.AppendLine("                    {");
        sb.AppendLine($"                        builder = new {builderType}();");
        sb.AppendLine("                        Configure(builder);");
        sb.AppendLine("                        global::System.Threading.Volatile.Write(ref this.__shiftMapperBuilder, builder);");
        sb.AppendLine("                    }");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            return builder;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Per-property customization hook — implement in your partial class half. Registering a member suppresses the generated convention for it.</summary>");
        sb.AppendLine($"    partial void Configure({builderType} map);");
        sb.AppendLine();
        // What the generator actually baked. AddConfiguration checks anything registered at runtime against
        // these, so a registration the generator never saw fails loudly instead of silently doing nothing.
        var bakedCustom = directives.ViewCustom.Concat(directives.EntityCustom)
            .Concat(directives.ListCustom).Concat(directives.CopyCustom);

        var bakedIgnored = directives.ViewIgnore.Concat(directives.EntityIgnore)
            .Concat(directives.ListIgnore).Concat(directives.CopyIgnore);

        sb.AppendLine($"    private static readonly string[] __shiftBakedCustom = {{ {Literals(bakedCustom)} }};");
        sb.AppendLine($"    private static readonly string[] __shiftBakedIgnored = {{ {Literals(bakedIgnored)} }};");
        sb.AppendLine();

        sb.AppendLine($"    void {configurableInterface}.AddConfiguration(global::System.Action<{builderType}> configure)");
        sb.AppendLine("    {");
        sb.AppendLine("        configure(this.__ShiftMap);");
        sb.AppendLine($"        this.__ShiftMap.VerifyBaked(__shiftBakedCustom, __shiftBakedIgnored, \"{className}\");");
        sb.AppendLine("        this.__shiftComposedListProjection = null;");
        sb.AppendLine("    }");
        sb.AppendLine();

        return sb;
    }

    /// <summary>Renders member names as a comma-separated list of C# string literals.</summary>
    private static string Literals(IEnumerable<string> names) =>
        string.Join(", ", names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => SymbolDisplay.FormatLiteral(n, quote: true)));

    /// <summary>Convention RHS for a view-direction member, or null (accessor = the source parameter name).</summary>
    private static string? ViewConvention(Dictionary<string, IPropertySymbol> entityProps, IPropertySymbol dtoProp, Compilation compilation, string accessor = "entity")
    {
        if (IsShiftType(dtoProp.Type, "ShiftEntitySelectDTO"))
        {
            if (entityProps.TryGetValue(dtoProp.Name + "ID", out var fk) && (IsLong(fk.Type) || IsNullableLong(fk.Type)))
            {
                var text = entityProps.TryGetValue(dtoProp.Name, out var nav) && TextPropertyOf(nav.Type) is { } textProp
                    ? $", {accessor}.{dtoProp.Name} != null ? {accessor}.{dtoProp.Name}.{textProp} : null"
                    : "";
                return $"{Helpers}.ToSelectDTO({accessor}.{dtoProp.Name}ID{text})";
            }

            return null;
        }

        if (IsShiftFileList(dtoProp.Type))
            return entityProps.TryGetValue(dtoProp.Name, out var src) && src.Type.SpecialType == SpecialType.System_String
                ? $"{Helpers}.ToShiftFiles({accessor}.{dtoProp.Name})"
                : null;

        if (!entityProps.TryGetValue(dtoProp.Name, out var match))
            return null;

        if (IsImplicit(compilation, match.Type, dtoProp.Type))
            return $"{accessor}.{dtoProp.Name}";

        if (UnwrapNullable(match.Type) is { } narrowed && SymbolEqualityComparer.Default.Equals(narrowed, dtoProp.Type))
            return $"{accessor}.{dtoProp.Name} ?? default";

        // long / long? → string and enum → int(?) — useful for pair DTOs that don't get MapBaseFields.
        if (dtoProp.Type.SpecialType == SpecialType.System_String && IsLong(match.Type))
            return $"{accessor}.{dtoProp.Name}.ToString()";

        if (dtoProp.Type.SpecialType == SpecialType.System_String && IsNullableLong(match.Type))
            return $"{accessor}.{dtoProp.Name}.HasValue ? {accessor}.{dtoProp.Name}.Value.ToString() : null";

        // Guid / Guid? → string. Paired with ToGuid/ToNullableGuid on the write side: a conversion that exists
        // in only one direction is exactly the asymmetry this work is here to remove.
        if (dtoProp.Type.SpecialType == SpecialType.System_String && IsGuid(match.Type))
            return $"{accessor}.{dtoProp.Name}.ToString()";

        if (dtoProp.Type.SpecialType == SpecialType.System_String && IsNullableGuid(match.Type))
            return $"{accessor}.{dtoProp.Name}.HasValue ? {accessor}.{dtoProp.Name}.Value.ToString() : null";

        var srcEnum = match.Type.TypeKind == TypeKind.Enum ? match.Type : UnwrapNullable(match.Type) is { TypeKind: TypeKind.Enum } se ? se : null;
        if (srcEnum is not null)
        {
            if (dtoProp.Type.SpecialType == SpecialType.System_Int32 && match.Type.TypeKind == TypeKind.Enum)
                return $"(int){accessor}.{dtoProp.Name}";
            if (UnwrapNullable(dtoProp.Type)?.SpecialType == SpecialType.System_Int32)
                return $"(int?){accessor}.{dtoProp.Name}";
        }

        // Everything else the general engine can do: the wider scalar set, and collections of simple values
        // whose element or container type differs.
        var source = $"{accessor}.{dtoProp.Name}";

        return ScalarConversion(compilation, match.Type, dtoProp.Type, source, sqlTranslatable: false)
            ?? CollectionConversion(compilation, match.Type, dtoProp.Type, source, sqlTranslatable: false);
    }

    /// <summary>A different-class child pair (single object or collection element) eligible for deep composition.</summary>
    private static bool TryGetComposableChild(Dictionary<string, IPropertySymbol> entityProps, IPropertySymbol dtoProp,
        out ITypeSymbol childEntity, out ITypeSymbol childDto, out bool isCollection)
    {
        childEntity = null!;
        childDto = null!;
        isCollection = false;

        if (!entityProps.TryGetValue(dtoProp.Name, out var src))
            return false;

        if (TryGetAnyElement(dtoProp.Type, out var dtoElement) && TryGetAnyElement(src.Type, out var entityElement))
        {
            if (!IsPairable(entityElement, dtoElement))
                return false;

            childEntity = entityElement;
            childDto = dtoElement;
            isCollection = true;
            return true;
        }

        if (IsPairable(src.Type, dtoProp.Type) &&
            src.Type is INamedTypeSymbol { IsGenericType: false } && dtoProp.Type is INamedTypeSymbol { IsGenericType: false })
        {
            childEntity = src.Type;
            childDto = dtoProp.Type;
            return true;
        }

        return false;
    }

    // --------------------------------- general conversions ---------------------------------
    //
    // One place that answers "can a value of type A become type B, and how?", used by all three directions so
    // they cannot drift apart. Before this each direction hand-listed a few pairs, which is how long/string was
    // supported while int/string was not, and how some conversions ended up existing in one direction only.
    //
    // sqlTranslatable marks the LIST direction. That expression becomes SQL, so it may only use casts and
    // ToString(), never a helper method call: an untranslatable projection fails at query time, which is far
    // worse than the member being reported unmapped and mapped by hand.

    private static readonly HashSet<SpecialType> NumericTypes = new()
    {
        SpecialType.System_SByte, SpecialType.System_Byte,
        SpecialType.System_Int16, SpecialType.System_UInt16,
        SpecialType.System_Int32, SpecialType.System_UInt32,
        SpecialType.System_Int64, SpecialType.System_UInt64,
        SpecialType.System_Decimal, SpecialType.System_Single, SpecialType.System_Double,
    };

    private static bool IsNumeric(ITypeSymbol type) => NumericTypes.Contains(type.SpecialType);

    private static bool IsEnum(ITypeSymbol type) => type.TypeKind == TypeKind.Enum;

    /// <summary>Value types the runtime helpers can parse from, and format to, invariant text.</summary>
    private static bool IsTextConvertible(ITypeSymbol type) =>
        IsNumeric(type) ||
        type.SpecialType == SpecialType.System_Boolean ||
        type.SpecialType == SpecialType.System_DateTime ||
        (type is { ContainingNamespace.Name: "System" } &&
         type.Name is "Guid" or "DateTimeOffset" or "TimeSpan" or "DateOnly" or "TimeOnly");

    /// <summary>
    /// A C# expression converting <paramref name="expr"/> from <paramref name="source"/> to
    /// <paramref name="target"/>, or null when no convention applies (the member is then reported unmapped).
    /// </summary>
    private static string? ScalarConversion(Compilation compilation, ITypeSymbol source, ITypeSymbol target,
        string expr, bool sqlTranslatable)
    {
        if (IsImplicit(compilation, source, target))
            return expr;

        var sourceInner = UnwrapNullable(source);
        var targetInner = UnwrapNullable(target);

        // T? to T: the framework's long-standing "unset means default" rule.
        if (sourceInner is not null && SymbolEqualityComparer.Default.Equals(sourceInner, target))
            return expr + " ?? default";

        var from = sourceInner ?? source;
        var to = targetInner ?? target;
        var sourceIsNullable = sourceInner is not null;

        // -- to text --
        if (target.SpecialType == SpecialType.System_String)
        {
            if (!IsTextConvertible(from) && !IsEnum(from))
                return null;

            // In SQL only a plain ToString() (a CAST) is available, and only for numbers: casting a date or a
            // bool to text is provider-specific and would silently differ from the in-memory result.
            if (sqlTranslatable)
                return IsNumeric(from)
                    ? sourceIsNullable
                        ? $"{expr}.HasValue ? {expr}.Value.ToString() : null"
                        : $"{expr}.ToString()"
                    : null;

            if (IsEnum(from))
                return sourceIsNullable
                    ? $"{expr}.HasValue ? {expr}.Value.ToString() : null"
                    : $"{expr}.ToString()";

            return $"{Helpers}.ToInvariantText({expr})";
        }

        // -- from text --
        if (source.SpecialType == SpecialType.System_String)
        {
            // Parsing has no SQL equivalent worth trusting.
            if (sqlTranslatable)
                return null;

            var targetFq = Fq(to);

            if (IsEnum(to))
                return targetInner is not null
                    ? $"{Helpers}.ToNullableEnum<{targetFq}>({expr})"
                    : $"{Helpers}.ToEnum<{targetFq}>({expr})";

            if (IsTextConvertible(to))
                return targetInner is not null
                    ? $"{Helpers}.ToNullableValue<{targetFq}>({expr})"
                    : $"{Helpers}.ToValue<{targetFq}>({expr})";

            return null;
        }

        // -- number to number, and enum to number: an explicit cast, which SQL translates too --
        var castable =
            (IsNumeric(from) || IsEnum(from)) &&
            (IsNumeric(to) || IsEnum(to)) &&
            !(IsEnum(from) && IsEnum(to));   // two unrelated enums are not a conversion, they are a mistake

        if (!castable)
            return null;

        var cast = Fq(target);

        // A nullable source filling a non-nullable target has to decide what "unset" means; default matches
        // what the opposite direction produces for an unset member.
        return sourceIsNullable && targetInner is null
            ? $"({cast})({expr} ?? default)"
            : $"({cast}){expr}";
    }

    /// <summary>
    /// Materialises <paramref name="items"/> into the concrete collection type <paramref name="target"/>.
    /// <para>
    /// Everything used to be materialised with <c>ToList()</c> regardless of the target. Assigning a
    /// <c>List&lt;T&gt;</c> into a <c>HashSet&lt;T&gt;</c> property does not compile, so an entity with a
    /// HashSet navigation could not use generated deep mapping at all; arrays were not recognised as
    /// collections in the first place.
    /// </para>
    /// </summary>
    private static string? MaterializeCollection(ITypeSymbol target, ITypeSymbol element, string items)
    {
        if (target is IArrayTypeSymbol)
            return $"global::System.Linq.Enumerable.ToArray({items})";

        if (target is INamedTypeSymbol { IsGenericType: true } named)
        {
            var definition = named.ConstructedFrom.ToDisplayString();

            if (definition == "System.Collections.Generic.HashSet<T>")
                return $"new global::System.Collections.Generic.HashSet<{Fq(element)}>({items})";

            if (definition is "System.Collections.Generic.List<T>"
                or "System.Collections.Generic.IList<T>"
                or "System.Collections.Generic.ICollection<T>"
                or "System.Collections.Generic.IEnumerable<T>"
                or "System.Collections.Generic.IReadOnlyList<T>"
                or "System.Collections.Generic.IReadOnlyCollection<T>")
                return $"global::System.Linq.Enumerable.ToList({items})";
        }

        return null;   // an unknown container: report it rather than emit code that will not compile
    }

    /// <summary>
    /// Converts a collection of simple values whose element type and/or container type differ, e.g.
    /// <c>ICollection&lt;int&gt;</c> to <c>List&lt;int&gt;</c>, or <c>List&lt;int&gt;</c> to
    /// <c>List&lt;string&gt;</c>. Complex children are left alone: deep composition owns those.
    /// </summary>
    private static string? CollectionConversion(Compilation compilation, ITypeSymbol source, ITypeSymbol target,
        string expr, bool sqlTranslatable)
    {
        if (!TryGetAnyElement(source, out var sourceElement) || !TryGetAnyElement(target, out var targetElement))
            return null;

        if (IsPairable(sourceElement, targetElement))
            return null;

        string items;

        if (SymbolEqualityComparer.Default.Equals(sourceElement, targetElement))
        {
            items = expr;
        }
        else
        {
            if (ScalarConversion(compilation, sourceElement, targetElement, "__v", sqlTranslatable) is not { } perItem)
                return null;

            items = $"global::System.Linq.Enumerable.Select({expr}, __v => {perItem})";
        }

        if (MaterializeCollection(target, targetElement, items) is not { } materialized)
            return null;

        return $"{expr} == null ? null : {materialized}";
    }

    /// <summary>Element type of a collection, arrays included.</summary>
    private static bool TryGetAnyElement(ITypeSymbol type, out ITypeSymbol element)
    {
        if (type is IArrayTypeSymbol array)
        {
            element = array.ElementType;
            return true;
        }

        return TryGetElement(type, out element);
    }

    private static bool TryGetElement(ITypeSymbol type, out ITypeSymbol element)
    {
        element = null!;

        if (type is not INamedTypeSymbol { TypeArguments.Length: 1 } named ||
            named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return false;

        var argument = named.TypeArguments[0];

        var enumerable =
            named.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T ||
            named.AllInterfaces.Any(i =>
                i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T &&
                SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], argument));

        if (!enumerable)
            return false;

        element = argument;
        return true;
    }

    private static bool IsPairable(ITypeSymbol entityType, ITypeSymbol dtoType) =>
        entityType.TypeKind == TypeKind.Class && dtoType.TypeKind == TypeKind.Class &&
        entityType.SpecialType == SpecialType.None && dtoType.SpecialType == SpecialType.None &&
        !SymbolEqualityComparer.Default.Equals(entityType, dtoType) &&       // same class → identity assignment already applies
        !dtoType.IsAbstract &&
        !DerivesFromShiftEntityBase(dtoType) &&                               // the target must be a DTO, not an entity
        !IsShiftType(dtoType, "ShiftEntitySelectDTO") && !IsShiftType(dtoType, "ShiftFileDTO");

    // ─────────────────────────────────── emission: bodies ───────────────────────────────────

    /// <summary>
    /// <paramref name="Mapped"/> is the set of DTO members the view body actually assigns. It is what the
    /// entity direction is compared against for SHENGEN008: a member the view reads and the entity never
    /// writes back is the exact failure this whole effort exists to catch — displays fine, never saves.
    /// </summary>
    private sealed record ViewEmission(List<string> Lines, List<string> UsedPairKeys, List<string> Unmapped, List<string> Mapped);

    private static ViewEmission BuildViewBody(string ownerKey, ITypeSymbol entity, ITypeSymbol viewDto,
        string entityName, string viewName, string accessor,
        Dictionary<string, PairInfo> pairs, HashSet<string> skippedEdges, Compilation compilation,
        MapperDirectives directives, HashSet<string> attrIgnored, int ownerDepth, int maxDepth)
    {
        var entityProps = AllProps(entity).Where(IsReadable).ToDictionary(p => p.Name, p => p);
        var lines = new List<string>();
        var usedPairs = new List<string>();
        var unmapped = new List<string>();
        var mapped = new List<string>();

        void Emit(IPropertySymbol dtoProp, bool withConvention)
        {
            var name = dtoProp.Name;
            var propType = Fq(dtoProp.Type);

            // Ignore → OMIT the member (build-time removal; a complex child's subtree is pruned by never composing it).
            if (directives.IsIgnored(MapDir.View, name) || attrIgnored.Contains(name))
                return;

            // Customized (ForView, or explicit ForViewChild(ren)) → reference the runtime delegate directly.
            // No custom-vs-convention branch: the DECISION was made here at build time. The delegate keeps the
            // member's current value when unregistered, so a mapper used without its config never throws.
            if (directives.IsCustom(MapDir.View, name))
            {
                mapped.Add(name);
                lines.Add($"        dto.{name} = this.__ShiftMap.InvokeView<{propType}>({accessor}, context, \"{name}\", dto.{name});");
                lines.Add("");
                return;
            }

            if (withConvention)
            {
                var conv = ViewConvention(entityProps, dtoProp, compilation, accessor);
                if (conv is not null)
                {
                    mapped.Add(name);
                    lines.Add($"        dto.{name} = {conv};");   // baked convention — no dictionary, no branch
                    lines.Add("");
                    return;
                }

                // Automatic deep composition (view direction), up to maxDepth. A child object/collection is
                // composed through its source-generated pair; beyond the cap (or a cycle edge) it is left at
                // its default — an explicit ForViewChild(ren) still composes it past the cap.
                if (TryGetComposableChild(entityProps, dtoProp, out var childEntity, out var childDto, out var isCollection))
                {
                    var key = PairKey(childEntity, childDto);

                    if (ownerDepth + 1 <= maxDepth && !skippedEdges.Contains(ownerKey + "|" + name) &&
                        pairs.TryGetValue(key, out _))
                    {
                        usedPairs.Add(key);
                        mapped.Add(name);
                        var field = $"__shiftPair_{Fnv8(key)}";
                        var src = $"{accessor}.{name}";

                        // Materialise into the member's OWN container type. Everything used to become a List,
                        // which silently ruled out arrays and did not compile for a HashSet member.
                        var mapped_ = $"global::System.Linq.Enumerable.Select({src}, __c => {field}.Map(__c, context))";
                        var into = isCollection
                            ? MaterializeCollection(dtoProp.Type, childDto, mapped_) ?? $"global::System.Linq.Enumerable.ToList({mapped_})"
                            : null;

                        lines.Add(isCollection
                            ? $"        dto.{name} = {src} == null ? null : {into};"
                            : $"        dto.{name} = {src} == null ? null : {field}.Map({src}, context);");
                        lines.Add("");
                    }

                    return;   // composable child (composed or intentionally left for explicit/beyond-cap) — not "unmapped"
                }

                unmapped.Add(name);
                return;
            }

            // withConvention == false (base/handled field) and not customized → leave what MapBaseFields set.
        }

        var settable = AllProps(viewDto).Where(IsSettable).ToList();

        foreach (var dtoProp in settable.Where(p => !IsViewHandled(p)))
            Emit(dtoProp, withConvention: true);

        if (DerivesFrom(viewDto, "ShiftEntityViewAndUpsertDTO") && DerivesFrom(entity, "ShiftEntity"))
        {
            lines.Add($"        {Helpers}.MapBaseFields(dto, {accessor});");
            lines.Add("");
        }

        foreach (var dtoProp in settable.Where(IsViewHandled))
            Emit(dtoProp, withConvention: false);

        // Init-only members can be SCANNED but never ASSIGNED. The generated mapper builds the DTO and then sets
        // its properties, so an init-only setter is unreachable to it — AutoMapper reached them by reflection,
        // which is why moving a DTO across can silently lose them. They were invisible here too, so the mapper
        // dropped them AND said nothing. Reporting is all that can be done, and it is worth doing.
        foreach (var initOnly in AllProps(viewDto).Where(p => p.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: true }))
            if (!directives.IsIgnored(MapDir.View, initOnly.Name) && !attrIgnored.Contains(initOnly.Name) &&
                !IsViewHandled(initOnly))
                unmapped.Add($"{initOnly.Name} (init-only — the generated mapper cannot assign it; give it a settable setter)");

        return new ViewEmission(lines, usedPairs, unmapped, mapped);
    }

    private static string? EntityConvention(Dictionary<string, IPropertySymbol> dtoProps, IPropertySymbol entityProp, Compilation compilation, string accessor = "dto")
    {
        if (entityProp.Name.EndsWith("ID", StringComparison.Ordinal) && entityProp.Name.Length > 2)
        {
            var baseName = entityProp.Name.Substring(0, entityProp.Name.Length - 2);
            if (dtoProps.TryGetValue(baseName, out var select) && IsShiftType(select.Type, "ShiftEntitySelectDTO"))
            {
                if (IsLong(entityProp.Type))
                    return $"{Helpers}.ToForeignKey({accessor}.{baseName})";

                if (IsNullableLong(entityProp.Type))
                    return $"{Helpers}.ToNullableForeignKey({accessor}.{baseName})";
            }
        }

        if (!dtoProps.TryGetValue(entityProp.Name, out var dtoProp))
            return null;

        if (entityProp.Type.SpecialType == SpecialType.System_String && IsShiftFileList(dtoProp.Type))
            return $"{Helpers}.ToJsonString({accessor}.{entityProp.Name})";

        if (IsImplicit(compilation, dtoProp.Type, entityProp.Type))
            return $"{accessor}.{entityProp.Name}";

        if (UnwrapNullable(dtoProp.Type) is { } inner && SymbolEqualityComparer.Default.Equals(inner, entityProp.Type))
            return $"{accessor}.{entityProp.Name} ?? default";

        // ── the inverse of ViewConvention's narrowing conversions ──
        //
        // ViewConvention turns long → string and enum → int on the way out. Without the conversions back, this
        // method returned null for such a member, and a null return means NO ASSIGNMENT IS EMITTED AT ALL: the
        // member read back perfectly and silently never saved. That is the single most dangerous class of write
        // loss in the generator, which is why these land before the list/entity diagnostics are turned up.

        var source = $"{accessor}.{entityProp.Name}";
        var dtoIsString = dtoProp.Type.SpecialType == SpecialType.System_String;

        // string → long / long?  (the mirror of .ToString())
        if (dtoIsString && IsLong(entityProp.Type))
            return $"{Helpers}.ToLong({source})";

        if (dtoIsString && IsNullableLong(entityProp.Type))
            return $"{Helpers}.ToNullableLong({source})";

        // string → Guid / Guid?
        if (dtoIsString && IsGuid(entityProp.Type))
            return $"{Helpers}.ToGuid({source})";

        if (dtoIsString && IsNullableGuid(entityProp.Type))
            return $"{Helpers}.ToNullableGuid({source})";

        // int / int? → enum / enum?  (the mirror of the (int) cast)
        var targetIsEnum = entityProp.Type.TypeKind == TypeKind.Enum;
        var targetEnum = targetIsEnum ? entityProp.Type
            : UnwrapNullable(entityProp.Type) is { TypeKind: TypeKind.Enum } lifted ? lifted : null;

        if (targetEnum is not null)
        {
            var dtoIsInt = dtoProp.Type.SpecialType == SpecialType.System_Int32;
            var dtoIsNullableInt = UnwrapNullable(dtoProp.Type)?.SpecialType == SpecialType.System_Int32;

            if (dtoIsInt || dtoIsNullableInt)
            {
                var target = Fq(entityProp.Type);

                // A nullable int cannot fill a non-nullable enum without a decision; default is the enum's own
                // zero value, which is what the reverse direction produced for an unset member anyway.
                return dtoIsNullableInt && targetIsEnum
                    ? $"({target})({source} ?? default(int))"
                    : $"({target}){source}";
            }
        }

        // The write half of the general engine, so every conversion the read side performs has a way back.
        return ScalarConversion(compilation, dtoProp.Type, entityProp.Type, source, sqlTranslatable: false)
            ?? CollectionConversion(compilation, dtoProp.Type, entityProp.Type, source, sqlTranslatable: false);
    }

    /// <summary>
    /// <paramref name="ConsumedDto"/> is the set of DTO members the entity body actually reads. Compared with
    /// the view side's <c>Mapped</c> set, it is what SHENGEN008 reports on.
    /// </summary>
    private sealed record EntityEmission(List<string> Lines, List<string> UsedPairs, HashSet<string> ConsumedDto,
        List<(string Member, string ChildType, string BackReference)> ReplacedTrackedChildren);

    /// <summary>
    /// The name of a required (non-nullable) foreign key on <paramref name="child"/> pointing back at
    /// <paramref name="parent"/>, or null when there is no certainty of one.
    /// <para>
    /// Only a tracked entity with a required back-reference is reported: those are the rows that cannot be
    /// replaced, because the framework forces Restrict on every non-ownership foreign key. A plain POCO or an
    /// optional relationship replaces perfectly well, and warning about it would be noise.
    /// </para>
    /// </summary>
    private static string? RequiredBackReference(ITypeSymbol child, ITypeSymbol parent)
    {
        if (!DerivesFrom(child, "ShiftEntity"))
            return null;

        var expected = parent.Name + "ID";

        return AllProps(child).Any(p => p.Name == expected && IsLong(p.Type) && IsSettable(p))
            ? expected
            : null;
    }

    private static EntityEmission BuildEntityBody(string ownerKey, ITypeSymbol entity, ITypeSymbol viewDto,
        string entityName, string viewName, Compilation compilation, MapperDirectives directives, HashSet<string> attrIgnored,
        Dictionary<string, PairInfo> pairs, HashSet<string> skippedEdges, int ownerDepth, int maxDepth)
    {
        var dtoProps = AllProps(viewDto).Where(IsReadable).ToDictionary(p => p.Name, p => p);
        var lines = new List<string>();
        var usedPairs = new List<string>();
        var consumedDto = new HashSet<string>(StringComparer.Ordinal);

        // Children the automatic deep write REPLACES rather than reconciles, where doing so will break at save
        // time. Reported as SHENGEN010 — see the descriptor.
        var replacedTrackedChildren = new List<(string Member, string ChildType, string BackReference)>();

        // Which DTO member does writing this entity member read from? Same name, except a foreign key, which is
        // written from the select DTO named after it without the "ID" suffix.
        string DtoSourceOf(IPropertySymbol entityProp)
        {
            if (entityProp.Name.EndsWith("ID", StringComparison.Ordinal) && entityProp.Name.Length > 2)
            {
                var baseName = entityProp.Name.Substring(0, entityProp.Name.Length - 2);

                if (dtoProps.TryGetValue(baseName, out var select) && IsShiftType(select.Type, "ShiftEntitySelectDTO"))
                    return baseName;
            }

            return entityProp.Name;
        }

        void Emit(IPropertySymbol entityProp, bool withConvention)
        {
            var name = entityProp.Name;
            var propType = Fq(entityProp.Type);

            if (directives.IsIgnored(MapDir.Entity, name) || attrIgnored.Contains(name))
                return;

            if (directives.IsCustom(MapDir.Entity, name))
            {
                // A ForEntity delegate takes the whole DTO and may read anything on it, so the same-named member
                // is the most that can be claimed — and claiming it is right: the programmer has taken this
                // member over, and warning them about it would be noise.
                consumedDto.Add(DtoSourceOf(entityProp));

                if (IsEntityNavigation(entityProp.Type))
                {
                    // Navigation (e.g. ForEntityChildren): apply the customization but NEVER read existing.<nav>
                    // (avoids lazy loading) — a guard, not the value-fallback.
                    var cast = $"global::System.Func<{viewName}, global::ShiftSoftware.ShiftEntity.Core.MappingContext, {propType}>";
                    lines.Add($"        if (this.__ShiftMap.TryGetEntityValue(\"{name}\", out var __e_{name}))");
                    lines.Add($"            existing.{name} = (({cast})__e_{name})(dto, context);");
                }
                else
                {
                    lines.Add($"        existing.{name} = this.__ShiftMap.InvokeEntity<{propType}>(dto, existing, context, \"{name}\", existing.{name});");
                }

                lines.Add("");
                return;
            }

            if (withConvention)
            {
                var conv = EntityConvention(dtoProps, entityProp, compilation, "dto");
                if (conv is not null)
                {
                    consumedDto.Add(DtoSourceOf(entityProp));
                    lines.Add($"        existing.{name} = {conv};");   // baked convention
                    lines.Add("");
                    return;
                }

                // No convention → fall through to the automatic deep write below, exactly like the view side
                // composes after ViewConvention misses. A plain child object/collection (a JSON-owned type, an
                // owned entity, any POCO) is NOT a ShiftEntity navigation, so it arrives here — returning now
                // would silently drop it on the write side while the view side still composed it.
            }

            // Child object/collection with no customization → AUTOMATIC deep write (replace-with-new) up to
            // maxDepth. Every child DTO becomes a NEW child entity via the pair's MapBack (pair this with a
            // repository that owns the previous children, e.g. delete-and-recreate). Beyond the cap / a cycle
            // edge it is left untouched. Pipeline-owned members (Tags, the [NotMapped] flags, the key) never compose.
            if (!IsEntityExcluded(entityProp) && ownerDepth + 1 <= maxDepth &&
                !skippedEdges.Contains(ownerKey + "|" + name) &&
                TryGetEntityComposableChild(entityProp, dtoProps, out var childEntity, out var childDto, out var isCollection))
            {
                var key = PairKey(childEntity, childDto);

                if (pairs.TryGetValue(key, out _))
                {
                    usedPairs.Add(key);
                    consumedDto.Add(name);

                    if (RequiredBackReference(childEntity, entity) is { } backReference)
                        replacedTrackedChildren.Add((name, childEntity.Name, backReference));
                    var field = $"__shiftPair_{Fnv8(key)}";
                    var src = $"dto.{name}";
                    var childEntityFq = Fq(childEntity);

                    var rebuilt = $"global::System.Linq.Enumerable.Select({src}, __d => {field}.MapBack(__d, new {childEntityFq}(), context))";
                    var back = isCollection
                        ? MaterializeCollection(entityProp.Type, childEntity, rebuilt) ?? $"global::System.Linq.Enumerable.ToList({rebuilt})"
                        : null;

                    lines.Add(isCollection
                        ? $"        existing.{name} = {src} == null ? null : {back};"
                        : $"        existing.{name} = {src} == null ? null : {field}.MapBack({src}, new {childEntityFq}(), context);");
                    lines.Add("");
                }
            }
            // Pipeline-owned or beyond-cap navigation with no customization → leave existing.
        }

        var settable = AllProps(entity).Where(IsSettable).ToList();

        foreach (var entityProp in settable.Where(p => !IsEntityExcluded(p) && !IsEntityNavigation(p.Type)))
            Emit(entityProp, withConvention: true);

        foreach (var entityProp in settable.Where(p => IsEntityExcluded(p) || IsEntityNavigation(p.Type)))
            Emit(entityProp, withConvention: false);

        return new EntityEmission(lines, usedPairs, consumedDto, replacedTrackedChildren);
    }

    // Entity-side composable child: an entity navigation (collection or single) whose same-named DTO member is a
    // pairable different-class DTO (collection or single). MapBack requires a parameterless child-entity ctor.
    private static bool TryGetEntityComposableChild(IPropertySymbol entityProp, Dictionary<string, IPropertySymbol> dtoProps,
        out ITypeSymbol childEntity, out ITypeSymbol childDto, out bool isCollection)
    {
        childEntity = null!;
        childDto = null!;
        isCollection = false;

        if (!dtoProps.TryGetValue(entityProp.Name, out var dtoProp))
            return false;

        if (TryGetElement(entityProp.Type, out var entityElement) && TryGetElement(dtoProp.Type, out var dtoElement))
        {
            if (!IsPairable(entityElement, dtoElement) || entityElement is not INamedTypeSymbol { Constructors: var ctors } ||
                !ctors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
                return false;

            childEntity = entityElement;
            childDto = dtoElement;
            isCollection = true;
            return true;
        }

        if (IsPairable(entityProp.Type, dtoProp.Type) &&
            entityProp.Type is INamedTypeSymbol { IsGenericType: false, Constructors: var singleCtors } &&
            dtoProp.Type is INamedTypeSymbol { IsGenericType: false } &&
            singleCtors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
        {
            childEntity = entityProp.Type;
            childDto = dtoProp.Type;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The list projection, plus the members it could NOT project.
    /// <para>
    /// The unmapped half exists because the list direction used to fail in total silence: a member the
    /// generator could not map was simply absent from the projection, so the column arrived empty on the wire
    /// with no build output of any kind.
    /// </para>
    /// </summary>
    private sealed record ListEmission(List<string> Lines, List<string> Unmapped);

    private static ListEmission BuildListAssignments(ITypeSymbol entity, ITypeSymbol listDto, Compilation compilation,
        MapperDirectives directives, HashSet<string> attrIgnored, Func<string, MapperDirectives> dirFor,
        string accessor, int ownerDepth, int maxDepth, HashSet<string> pathKeys, string memberPath = "")
    {
        var entityProps = AllProps(entity).Where(IsReadable).ToDictionary(p => p.Name, p => p);
        var assignments = new List<string>();
        var unmapped = new List<string>();

        foreach (var dtoProp in AllProps(listDto).Where(p => IsSettable(p) && !IsFrameworkListMember(p)))
        {
            // Ignore → omit the binding entirely.
            if (directives.IsIgnored(MapDir.List, dtoProp.Name) || attrIgnored.Contains(dtoProp.Name))
                continue;

            // A ForList customization is layered on at runtime by ComposeList, so the member is HANDLED and must
            // not be reported as unmapped. It still gets its convention binding baked here: ComposeList replaces
            // that binding when the configuration is applied, and when the mapper is used WITHOUT its
            // configuration — resolved straight from the registry, with no repository — the convention is all
            // there is. Dropping the binding as well as the warning made that case return null.
            var customizedForList = directives.IsCustom(MapDir.List, dtoProp.Name);

            // FK → ShiftEntitySelectDTO, inlined so the projection stays SQL-translatable (the ToSelectDTO
            // helper is a method call EF can't translate; a member-init + navigation access it can). A
            // SelectDTO is a LEAF reference (id + name), so it maps by convention.
            if (IsShiftType(dtoProp.Type, "ShiftEntitySelectDTO") &&
                entityProps.TryGetValue(dtoProp.Name + "ID", out var listFk) && (IsLong(listFk.Type) || IsNullableLong(listFk.Type)))
            {
                const string selectDto = "global::ShiftSoftware.ShiftEntity.Model.Dtos.ShiftEntitySelectDTO";
                var text = entityProps.TryGetValue(dtoProp.Name, out var listNav) && TextPropertyOf(listNav.Type) is { } listTextProp
                    ? $"{accessor}.{dtoProp.Name} != null ? {accessor}.{dtoProp.Name}.{listTextProp} : null"
                    : "null";

                assignments.Add(IsLong(listFk.Type)
                    ? $"            {dtoProp.Name} = new {selectDto} {{ Value = {accessor}.{dtoProp.Name}ID.ToString(), Text = {text} }},"
                    : $"            {dtoProp.Name} = {accessor}.{dtoProp.Name}ID == null ? null : new {selectDto} {{ Value = {accessor}.{dtoProp.Name}ID.Value.ToString(), Text = {text} }},");
                continue;
            }

            // AUTOMATIC deep composition (list direction): a composable child collection/object is projected
            // INLINE as a correlated member-init (SQL-translatable), recursively, up to maxDepth. Skipped when
            // an explicit ForListChild(ren) is configured (that member is composed at runtime by ComposeList).
            if (TryGetComposableChild(entityProps, dtoProp, out var childEntity, out var childDto, out var isCollection))
            {
                var childKey = PairKey(childEntity, childDto);

                if (!directives.IsCustom(MapDir.List, dtoProp.Name) &&
                    ownerDepth + 1 <= maxDepth && !pathKeys.Contains(childKey))
                {
                    var src = $"{accessor}.{dtoProp.Name}";
                    var param = $"__l{ownerDepth}";
                    // Collection → the child is projected inside a Select lambda (param). Single object → the child
                    // is projected directly off the source navigation, so there is NO new parameter to introduce.
                    var childAccessor = isCollection ? param : src;
                    var childPath = new HashSet<string>(pathKeys, StringComparer.Ordinal) { childKey };
                    var child = BuildListAssignments(childEntity, childDto, compilation,
                        dirFor(TripleKey(childEntity, childDto, childDto)), CollectAttrIgnored(childEntity, childDto),
                        dirFor, childAccessor, ownerDepth + 1, maxDepth, childPath, memberPath + dtoProp.Name + ".");

                    unmapped.AddRange(child.Unmapped);
                    var childInit = $"new {Fq(childDto)}\n            {{\n{string.Join("\n", child.Lines)}\n            }}";

                    var projected = $"global::System.Linq.Enumerable.Select({src}, {param} => {childInit})";
                    var listInto = isCollection
                        ? MaterializeCollection(dtoProp.Type, childDto, projected) ?? $"global::System.Linq.Enumerable.ToList({projected})"
                        : null;

                    assignments.Add(isCollection
                        ? $"            {dtoProp.Name} = {src} == null ? null : {listInto},"
                        : $"            {dtoProp.Name} = {src} == null ? null : {childInit},");
                }

                continue;   // composable child handled (composed, or left for ComposeList / beyond cap / cycle)
            }

            if (!entityProps.TryGetValue(dtoProp.Name, out var src2))
            {
                if (!customizedForList)
                    unmapped.Add(SuggestListFix(entityProps, dtoProp, memberPath));

                continue;
            }

            if (IsImplicit(compilation, src2.Type, dtoProp.Type))
            {
                assignments.Add($"            {dtoProp.Name} = {accessor}.{dtoProp.Name},");
                continue;
            }

            if (UnwrapNullable(src2.Type) is { } narrowed && SymbolEqualityComparer.Default.Equals(narrowed, dtoProp.Type))
            {
                assignments.Add($"            {dtoProp.Name} = {accessor}.{dtoProp.Name} ?? default,");
                continue;
            }

            if (dtoProp.Type.SpecialType == SpecialType.System_String && IsLong(src2.Type))
            {
                assignments.Add($"            {dtoProp.Name} = {accessor}.{dtoProp.Name}.ToString(),");
                continue;
            }

            if (dtoProp.Type.SpecialType == SpecialType.System_String && IsNullableLong(src2.Type))
            {
                assignments.Add($"            {dtoProp.Name} = {accessor}.{dtoProp.Name}.HasValue ? {accessor}.{dtoProp.Name}.Value.ToString() : null,");
                continue;
            }

            if (dtoProp.Type.SpecialType == SpecialType.System_String && IsGuid(src2.Type))
            {
                assignments.Add($"            {dtoProp.Name} = {accessor}.{dtoProp.Name}.ToString(),");
                continue;
            }

            if (dtoProp.Type.SpecialType == SpecialType.System_String && IsNullableGuid(src2.Type))
            {
                assignments.Add($"            {dtoProp.Name} = {accessor}.{dtoProp.Name}.HasValue ? {accessor}.{dtoProp.Name}.Value.ToString() : null,");
                continue;
            }

            var srcEnum = src2.Type.TypeKind == TypeKind.Enum ? src2.Type : UnwrapNullable(src2.Type) is { TypeKind: TypeKind.Enum } se ? se : null;
            if (srcEnum is not null)
            {
                if (dtoProp.Type.SpecialType == SpecialType.System_Int32 && src2.Type.TypeKind == TypeKind.Enum)
                {
                    assignments.Add($"            {dtoProp.Name} = (int){accessor}.{dtoProp.Name},");
                    continue;
                }

                if (UnwrapNullable(dtoProp.Type)?.SpecialType == SpecialType.System_Int32)
                {
                    assignments.Add($"            {dtoProp.Name} = (int?){accessor}.{dtoProp.Name},");
                    continue;
                }
            }

            // The general engine, restricted to what EF can turn into SQL.
            var listSource = $"{accessor}.{dtoProp.Name}";

            var general =
                ScalarConversion(compilation, src2.Type, dtoProp.Type, listSource, sqlTranslatable: true)
                ?? CollectionConversion(compilation, src2.Type, dtoProp.Type, listSource, sqlTranslatable: true);

            if (general is not null)
            {
                assignments.Add($"            {dtoProp.Name} = {general},");
                continue;
            }

            // A same-named entity member exists but nothing can convert it. Still an empty column, still worth
            // saying so — unless the programmer already took the member over with ForList.
            if (!customizedForList)
                unmapped.Add(SuggestListFix(entityProps, dtoProp, memberPath));
        }

        return new ListEmission(assignments, unmapped);
    }

    /// <summary>
    /// Describes one unprojectable list member as a paste-ready fix line.
    /// <para>
    /// This is what makes the deliberate decision NOT to implement flattening affordable. Rather than reaching
    /// two levels into the entity behind the programmer's back — invisible coupling, and lazy loads in the view
    /// direction — the compiler prints the exact line to paste. Where the member name looks like a flattened
    /// path (<c>CampaignName</c> → <c>Campaign.Name</c>) the suggestion is filled in; otherwise it is left as a
    /// blank for the programmer.
    /// </para>
    /// </summary>
    private static string SuggestListFix(Dictionary<string, IPropertySymbol> entityProps, IPropertySymbol dtoProp, string memberPath)
    {
        var name = dtoProp.Name;
        var suggestion = FlattenedPath(entityProps, name) ?? "…";

        return $"{memberPath}{name} (map.ForList(d => d.{name}, e => e.{suggestion}))";
    }

    /// <summary>
    /// Splits a member name at each position and returns the first "Nav.Member" that actually exists on the
    /// entity — the classic flattening convention, used here ONLY to word the suggestion.
    /// </summary>
    private static string? FlattenedPath(Dictionary<string, IPropertySymbol> entityProps, string name)
    {
        for (var split = 1; split < name.Length; split++)
        {
            var head = name.Substring(0, split);
            var tail = name.Substring(split);

            if (!entityProps.TryGetValue(head, out var nav) || nav.Type.TypeKind != TypeKind.Class)
                continue;

            if (AllProps(nav.Type).Any(p => IsReadable(p) && p.Name == tail))
                return $"{head}.{tail}";
        }

        return null;
    }

    // CopyEntity is a TOP-LEVEL (shallow) copy — entity → entity, same type, so every property is copied as-is
    // (scalars + navigation REFERENCES), excluding keys/flags. No auto deep-clone: it's used by ReloadAfterSave
    // (a faithful refresh — nav references keep real keys) and copying child collections by reference is correct
    // there. Per-member copy customization is ForCopy / IgnoreCopy / [ShiftEntityMapperIgnore].
    private static List<string> BuildCopyBody(ITypeSymbol entity, string entityName, MapperDirectives directives, HashSet<string> attrIgnored)
    {
        var lines = new List<string>();

        void Emit(IPropertySymbol prop, bool withConvention)
        {
            var name = prop.Name;
            var propType = Fq(prop.Type);

            if (directives.IsIgnored(MapDir.Copy, name) || attrIgnored.Contains(name))
                return;

            if (directives.IsCustom(MapDir.Copy, name))
            {
                lines.Add($"        target.{name} = this.__ShiftMap.InvokeCopy<{propType}>(source, context, \"{name}\", target.{name});");
                lines.Add("");
                return;
            }

            if (withConvention)
            {
                lines.Add($"        target.{name} = source.{name};");   // baked copy (scalar / navigation reference)
                lines.Add("");
            }
            // Excluded member (key/flags) with no customization → keep target's value (e.g. ReloadAfterSave).
        }

        var copyable = AllProps(entity).Where(p => IsReadable(p) && IsSettable(p)).ToList();

        foreach (var prop in copyable.Where(p => !IsCopyExcluded(p)))
            Emit(prop, withConvention: true);

        foreach (var prop in copyable.Where(IsCopyExcluded))
            Emit(prop, withConvention: false);

        return lines;
    }

    private static void AppendPairFields(StringBuilder sb, IEnumerable<string> usedPairKeys, Dictionary<string, PairInfo> pairs)
    {
        foreach (var key in usedPairKeys.Distinct())
        {
            var pair = pairs[key];
            sb.AppendLine($"    private static readonly {pair.TypeRef} __shiftPair_{Fnv8(key)} = new {pair.TypeRef}();");
        }

        sb.AppendLine();
    }

    // ─────────────────────────────────── emission: pair mappers ───────────────────────────────────

    private static void EmitPair(SourceProductionContext spc, string key, PairInfo pair,
        Dictionary<string, PairInfo> pairs, HashSet<string> skippedEdges, Compilation compilation,
        MapperDirectives directives, Func<string, MapperDirectives> dirFor, int ownerDepth, int maxDepth)
    {
        var entityName = Fq(pair.Entity);
        var dtoName = Fq(pair.Dto);
        var builderType = $"global::ShiftSoftware.ShiftEntity.Core.ShiftMapperBuilder<{entityName}, {dtoName}, {dtoName}>";
        var configurable = $"global::ShiftSoftware.ShiftEntity.Core.IShiftMapperConfigurable<{entityName}, {dtoName}, {dtoName}>";

        string? ns;
        string declaration;

        if (pair.UserClass is not null)
        {
            ns = pair.UserClass.ContainingNamespace.IsGlobalNamespace ? null : pair.UserClass.ContainingNamespace.ToDisplayString();
            declaration = $"partial class {pair.ClassName} : {configurable}";
        }
        else
        {
            ns = AutoNamespace;
            declaration = $"internal sealed partial class {pair.ClassName} : global::ShiftSoftware.ShiftEntity.Core.IShiftObjectMapper<{entityName}, {dtoName}>, {configurable}";
        }

        var sb = StartClass(ns, pair.ClassName, declaration, builderType, configurable, entityName, dtoName, directives);

        var attrIgnored = CollectAttrIgnored(pair.Entity, pair.Dto);
        var view = BuildViewBody(key, pair.Entity, pair.Dto, entityName, dtoName, "source", pairs, skippedEdges, compilation, directives, attrIgnored, ownerDepth, maxDepth);
        var entityBody = BuildEntityBody(key, pair.Entity, pair.Dto, entityName, dtoName, compilation, directives, attrIgnored, pairs, skippedEdges, ownerDepth, maxDepth);
        AppendPairFields(sb, view.UsedPairKeys.Concat(entityBody.UsedPairs).ToList(), pairs);

        var hasUserMap = pair.UserClass is not null && HasUserMethod(pair.UserClass, "Map");
        var hasUserMapGen = pair.UserClass is not null && HasUserMethod(pair.UserClass, "MapGenerated");
        var hasUserMapBack = pair.UserClass is not null && HasUserMethod(pair.UserClass, "MapBack");
        var hasUserMapBackGen = pair.UserClass is not null && HasUserMethod(pair.UserClass, "MapBackGenerated");

        if (!hasUserMapGen)
        {
            sb.AppendLine($"    private {dtoName} MapGenerated({entityName} source, global::ShiftSoftware.ShiftEntity.Core.MappingContext context = default)");
            sb.AppendLine("    {");
            sb.AppendLine($"        var dto = new {dtoName}();");
            sb.AppendLine();
            sb.Append(string.Join("\n", view.Lines));
            sb.AppendLine("        return dto;");
            sb.AppendLine("    }");
            sb.AppendLine();

            if (!hasUserMap)
            {
                sb.AppendLine($"    public {dtoName} Map({entityName} source, global::ShiftSoftware.ShiftEntity.Core.MappingContext context = default)");
                sb.AppendLine("        => MapGenerated(source, context);");
                sb.AppendLine();
            }
        }

        if (!hasUserMapBackGen)
        {
            sb.AppendLine($"    private {entityName} MapBackGenerated({dtoName} dto, {entityName} existing, global::ShiftSoftware.ShiftEntity.Core.MappingContext context = default)");
            sb.AppendLine("    {");
            sb.Append(string.Join("\n", entityBody.Lines));
            sb.AppendLine("        this.__ShiftMap.RunAfterEntity(dto, existing, context);");
            sb.AppendLine("        return existing;");
            sb.AppendLine("    }");
            sb.AppendLine();

            if (!hasUserMapBack)
            {
                sb.AppendLine($"    public {entityName} MapBack({dtoName} dto, {entityName} existing, global::ShiftSoftware.ShiftEntity.Core.MappingContext context = default)");
                sb.AppendLine("        => MapBackGenerated(dto, existing, context);");
                sb.AppendLine();
            }
        }

        // Conventions-only, SQL-translatable projection — used by ForListChildren/ForListChild.
        var pairList = BuildListAssignments(pair.Entity, pair.Dto, compilation, directives, attrIgnored,
            dirFor, "e", ownerDepth, maxDepth, new HashSet<string>(StringComparer.Ordinal) { key });

        sb.AppendLine($"    public static readonly global::System.Linq.Expressions.Expression<global::System.Func<{entityName}, {dtoName}>> Projection = e => new {dtoName}");
        sb.AppendLine("    {");
        sb.Append(string.Join("\n", pairList.Lines));
        sb.AppendLine();
        sb.AppendLine("    };");
        sb.AppendLine("}");

        sb.AppendLine();
        sb.AppendLine($"internal static class {pair.ClassName}Registration");
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");
        sb.AppendLine("        global::ShiftSoftware.ShiftEntity.Core.ShiftEntityMapperRegistry.RegisterPair(");
        sb.AppendLine($"            typeof({entityName}), typeof({dtoName}), typeof({pair.TypeRef.TrimStart()}), {pair.TypeRef}.Projection);");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        if (view.Unmapped.Count > 0)
            spc.ReportDiagnostic(Diagnostic.Create(UnmappedMembers,
                pair.UserClass?.Locations.FirstOrDefault() ?? pair.OriginLocation ?? Location.None,
                pair.ClassName, string.Join(", ", view.Unmapped)));

        if (pairList.Unmapped.Count > 0)
            spc.ReportDiagnostic(Diagnostic.Create(UnmappedListMembers,
                pair.UserClass?.Locations.FirstOrDefault() ?? pair.OriginLocation ?? Location.None,
                pair.ClassName, string.Join(", ", pairList.Unmapped)));

        // Namespace-prefixed: two same-named pairs in different namespaces produce the same class name, and
        // duplicate hint names make the generator fail rather than emit both.
        spc.AddSource($"{(ns ?? "global").Replace('.', '_')}_{pair.ClassName}.g.cs", sb.ToString());
    }

    // ─────────────────────────────────── emission: triple mappers ───────────────────────────────────

    private static void EmitTriple(SourceProductionContext spc, string key, TripleModel triple, INamedTypeSymbol? userClass,
        Dictionary<string, PairInfo> pairs, HashSet<string> skippedEdges, Compilation compilation,
        MapperDirectives directives, Func<string, MapperDirectives> dirFor, int maxDepth)
    {
        var entityName = Fq(triple.Entity);
        var listName = Fq(triple.ListDto);
        var viewName = Fq(triple.ViewDto);
        var builderType = $"global::ShiftSoftware.ShiftEntity.Core.ShiftMapperBuilder<{entityName}, {listName}, {viewName}>";
        var configurable = $"global::ShiftSoftware.ShiftEntity.Core.IShiftMapperConfigurable<{entityName}, {listName}, {viewName}>";

        string? ns;
        string className;
        string declaration;

        if (userClass is not null)
        {
            ns = userClass.ContainingNamespace.IsGlobalNamespace ? null : userClass.ContainingNamespace.ToDisplayString();
            className = userClass.Name;
            declaration = $"partial class {className} : {configurable}";
        }
        else
        {
            ns = AutoNamespace;
            className = $"Generated_{triple.Entity.Name}_{triple.ListDto.Name}_{triple.ViewDto.Name}_{Fnv8(key)}";
            declaration = $"internal sealed partial class {className} : global::ShiftSoftware.ShiftEntity.Core.IShiftEntityMapper<{entityName}, {listName}, {viewName}>, {configurable}";
        }

        var typeRef = userClass is not null ? Fq(userClass) : $"global::{AutoNamespace}.{className}";

        var sb = StartClass(ns, className, declaration, builderType, configurable, entityName, listName, directives);

        var attrIgnored = CollectAttrIgnored(triple.Entity, triple.ViewDto, triple.ListDto);
        var view = BuildViewBody(key, triple.Entity, triple.ViewDto, entityName, viewName, "entity", pairs, skippedEdges, compilation, directives, attrIgnored, 0, maxDepth);
        var entityBody = BuildEntityBody(key, triple.Entity, triple.ViewDto, entityName, viewName, compilation, directives, attrIgnored, pairs, skippedEdges, 0, maxDepth);
        AppendPairFields(sb, view.UsedPairKeys.Concat(entityBody.UsedPairs).ToList(), pairs);

        bool HasUser(string name) => userClass is not null && HasUserMethod(userClass, name);

        if (!HasUser("MapToViewGenerated"))
        {
            sb.AppendLine($"    private {viewName} MapToViewGenerated({entityName} entity, global::ShiftSoftware.ShiftEntity.Core.MappingContext context = default)");
            sb.AppendLine("    {");
            sb.AppendLine($"        var dto = new {viewName}();");
            sb.AppendLine();
            sb.Append(string.Join("\n", view.Lines));
            sb.AppendLine("        return dto;");
            sb.AppendLine("    }");
            sb.AppendLine();

            if (!HasUser("MapToView"))
            {
                sb.AppendLine($"    public {viewName} MapToView({entityName} entity, global::ShiftSoftware.ShiftEntity.Core.MappingContext context = default)");
                sb.AppendLine("        => MapToViewGenerated(entity, context);");
                sb.AppendLine();
            }
        }

        if (!HasUser("MapToEntityGenerated"))
        {
            sb.AppendLine($"    private {entityName} MapToEntityGenerated({viewName} dto, {entityName} existing, global::ShiftSoftware.ShiftEntity.Core.MappingContext context = default)");
            sb.AppendLine("    {");
            sb.Append(string.Join("\n", entityBody.Lines));
            sb.AppendLine("        this.__ShiftMap.RunAfterEntity(dto, existing, context);");
            sb.AppendLine("        return existing;");
            sb.AppendLine("    }");
            sb.AppendLine();

            if (!HasUser("MapToEntity"))
            {
                sb.AppendLine($"    public {entityName} MapToEntity({viewName} dto, {entityName} existing, global::ShiftSoftware.ShiftEntity.Core.MappingContext context = default)");
                sb.AppendLine("        => MapToEntityGenerated(dto, existing, context);");
                sb.AppendLine();
            }
        }

        if (!HasUser("MapToListGenerated"))
        {
            var taggable =
                triple.Entity.AllInterfaces.Any(i => i.Name == "IShiftEntityTaggable") &&
                triple.ListDto.AllInterfaces.Any(i => i.Name == "IShiftEntityTaggableDTO");

            var select = taggable ? $"{TaggableExtensions}.SelectWithTags" : "global::System.Linq.Queryable.Select";

            var listEmission = BuildListAssignments(triple.Entity, triple.ListDto, compilation, directives, attrIgnored,
                dirFor, "e", 0, maxDepth, new HashSet<string>(StringComparer.Ordinal));

            if (listEmission.Unmapped.Count > 0)
                spc.ReportDiagnostic(Diagnostic.Create(UnmappedListMembers,
                    userClass?.Locations.FirstOrDefault() ?? triple.RepoLocation ?? Location.None,
                    className, string.Join(", ", listEmission.Unmapped)));

            sb.AppendLine($"    private static readonly global::System.Linq.Expressions.Expression<global::System.Func<{entityName}, {listName}>> __shiftListProjection = e => new {listName}");
            sb.AppendLine("    {");
            sb.Append(string.Join("\n", listEmission.Lines));
            sb.AppendLine();
            sb.AppendLine("    };");
            sb.AppendLine();
            sb.AppendLine($"    private global::System.Linq.IQueryable<{listName}> MapToListGenerated(global::System.Linq.IQueryable<{entityName}> queryable, global::ShiftSoftware.ShiftEntity.Core.MappingContext context = default)");
            sb.AppendLine("    {");
            sb.AppendLine("        var projection = this.__shiftComposedListProjection;");
            sb.AppendLine();
            sb.AppendLine("        if (projection == null)");
            sb.AppendLine("        {");
            sb.AppendLine("            projection = this.__ShiftMap.ComposeList(__shiftListProjection);");
            sb.AppendLine("            this.__shiftComposedListProjection = projection;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        return {select}(queryable, projection);");
            sb.AppendLine("    }");
            sb.AppendLine();

            if (!HasUser("MapToList"))
            {
                sb.AppendLine($"    public global::System.Linq.IQueryable<{listName}> MapToList(global::System.Linq.IQueryable<{entityName}> queryable, global::ShiftSoftware.ShiftEntity.Core.MappingContext context = default)");
                sb.AppendLine("        => MapToListGenerated(queryable, context);");
                sb.AppendLine();
            }
        }

        if (!HasUser("CopyEntityGenerated"))
        {
            sb.AppendLine($"    private void CopyEntityGenerated({entityName} source, {entityName} target, global::ShiftSoftware.ShiftEntity.Core.MappingContext context = default)");
            sb.AppendLine("    {");
            sb.Append(string.Join("\n", BuildCopyBody(triple.Entity, entityName, directives, attrIgnored)));
            sb.AppendLine("    }");
            sb.AppendLine();

            if (!HasUser("CopyEntity"))
            {
                sb.AppendLine($"    public void CopyEntity({entityName} source, {entityName} target, global::ShiftSoftware.ShiftEntity.Core.MappingContext context = default)");
                sb.AppendLine("        => CopyEntityGenerated(source, target, context);");
                sb.AppendLine();
            }
        }

        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"internal static class {className}Registration");
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");
        sb.AppendLine("        global::ShiftSoftware.ShiftEntity.Core.ShiftEntityMapperRegistry.Register(");
        sb.AppendLine($"            typeof({entityName}), typeof({listName}), typeof({viewName}), typeof({typeRef}));");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        if (view.Unmapped.Count > 0)
            spc.ReportDiagnostic(Diagnostic.Create(UnmappedMembers,
                userClass?.Locations.FirstOrDefault() ?? triple.RepoLocation ?? Location.None,
                className, string.Join(", ", view.Unmapped)));

        // READ/WRITE ASYMMETRY. Deliberately NOT a mirror of SHENGEN004: BuildEntityBody walks ENTITY properties,
        // so mirroring it would warn about every internal and computed column and be useless. What is actionable
        // is the other set — DTO members the view reads and the entity never writes back. That is precisely the
        // failure this effort exists to prevent: the field displays correctly and silently fails to save.
        //
        // Legitimate asymmetries do exist (a computed, read-only DTO member). IgnoreEntity is how they are
        // declared, which turns each one into an explicit, reviewed decision instead of an accident.
        var neverWritten = view.Mapped
            .Where(member => !entityBody.ConsumedDto.Contains(member))
            .Where(member => !directives.IsIgnored(MapDir.Entity, member) && !attrIgnored.Contains(member))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (neverWritten.Count > 0)
            spc.ReportDiagnostic(Diagnostic.Create(EntityWriteAsymmetry,
                userClass?.Locations.FirstOrDefault() ?? triple.RepoLocation ?? Location.None,
                className, string.Join(", ", neverWritten)));

        // Automatic deep write is replace-with-new. Keeping that default is right — turning it off re-opens the
        // silent-empty-on-save bug it was introduced to fix — so the fix is to make the dangerous case VISIBLE
        // and give it an escape hatch, which is what AfterEntity and the existing-aware ForEntity are for.
        foreach (var (member, childType, backReference) in entityBody.ReplacedTrackedChildren)
            spc.ReportDiagnostic(Diagnostic.Create(DeepWriteReplacesTrackedChildren,
                userClass?.Locations.FirstOrDefault() ?? triple.RepoLocation ?? Location.None,
                className, member, childType, backReference));

        var hintName = userClass is not null
            ? $"{(ns ?? "global").Replace('.', '_')}_{className}.g.cs"
            : $"{className}.g.cs";

        spc.AddSource(hintName, sb.ToString());
    }

    // ─────────────────────────────────── symbol helpers ───────────────────────────────────

    private static string Fq(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static bool IsShiftNamespace(INamedTypeSymbol symbol) =>
        symbol.ContainingNamespace.ToDisplayString().StartsWith("ShiftSoftware.ShiftEntity", StringComparison.Ordinal);

    private static bool HasUserMethod(INamedTypeSymbol cls, string name) =>
        cls.GetMembers(name).OfType<IMethodSymbol>().Any(m => m.MethodKind == MethodKind.Ordinary);

    private static IEnumerable<IPropertySymbol> AllProps(ITypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var current = type; current is not null; current = current.BaseType)
            foreach (var p in current.GetMembers().OfType<IPropertySymbol>())
                if (!p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public && seen.Add(p.Name))
                    yield return p;
    }

    private static bool IsSettable(IPropertySymbol p) =>
        p.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false };

    private static bool IsReadable(IPropertySymbol p) =>
        p.GetMethod is { DeclaredAccessibility: Accessibility.Public };

    private static bool IsShiftType(ITypeSymbol type, string name) =>
        type is INamedTypeSymbol named && named.Name == name && IsShiftNamespace(named);

    private static bool IsShiftFileList(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "List", TypeArguments.Length: 1 } named &&
        IsShiftType(named.TypeArguments[0], "ShiftFileDTO");

    private static bool IsLong(ITypeSymbol type) => type.SpecialType == SpecialType.System_Int64;

    private static bool IsNullableLong(ITypeSymbol type) =>
        UnwrapNullable(type)?.SpecialType == SpecialType.System_Int64;

    private static bool IsGuid(ITypeSymbol type) =>
        type is { Name: "Guid", ContainingNamespace.Name: "System" };

    private static bool IsNullableGuid(ITypeSymbol type) =>
        UnwrapNullable(type) is { } inner && IsGuid(inner);

    private static ITypeSymbol? UnwrapNullable(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } named
            ? named.TypeArguments[0]
            : null;

    private static bool IsImplicit(Compilation compilation, ITypeSymbol source, ITypeSymbol destination)
    {
        var conversion = compilation.ClassifyCommonConversion(source, destination);
        return conversion.Exists && (conversion.IsIdentity || conversion.IsImplicit);
    }

    private static bool DerivesFrom(ITypeSymbol type, string baseName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (current.Name == baseName && IsShiftNamespace(current))
                return true;

        return false;
    }

    /// <summary>
    /// The property to read a select DTO's display text from, or null when there is none.
    /// <para>
    /// <c>[ShiftEntityKeyAndName]</c> names it explicitly; only when the attribute is absent is "Name" assumed.
    /// The attribute used to be ignored here, so a type that named a different text property was read from the
    /// wrong one — or, having no "Name" at all, from none.
    /// </para>
    /// </summary>
    private static string? TextPropertyOf(ITypeSymbol type)
    {
        var declared = type.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "ShiftEntityKeyAndNameAttribute")
            ?.ConstructorArguments is { Length: 2 } args && args[1].Value is string text
            ? text
            : null;

        var candidate = declared ?? "Name";

        return AllProps(type).Any(p => p.Name == candidate && p.Type.SpecialType == SpecialType.System_String && IsReadable(p))
            ? candidate
            : null;
    }

    private static bool IsEntityNavigation(ITypeSymbol type)
    {
        if (DerivesFromShiftEntityBase(type))
            return true;

        return type is INamedTypeSymbol { TypeArguments.Length: 1 } named && DerivesFromShiftEntityBase(named.TypeArguments[0]);
    }

    private static bool DerivesFromShiftEntityBase(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.Name == "ShiftEntityBase" && current is INamedTypeSymbol named && IsShiftNamespace(named))
                return true;

        return false;
    }

    private static string Fnv8(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619u;
            }
            return hash.ToString("x8");
        }
    }
}
