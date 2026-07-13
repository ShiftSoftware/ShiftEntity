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
///    (grandchildren) with cycle detection, and COMPOSED automatically into the parents' MapToView.
///    Declared [ShiftEntityMapper] partial pair classes replace the auto ones. Entity/list directions
///    are never composed automatically (persistence and query-shape decisions) — the pair exposes
///    MapBack and a conventions-only static list Projection for the explicit builder sugar
///    (ForEntityChildren / ForListChildren).
///
/// Conventions: scalars by name + implicit conversion; entity T? → DTO T narrowing (?? default);
/// long/long? → string and enum → int(?); FK ↔ ShiftEntitySelectDTO via MappingHelpers; string ↔
/// List&lt;ShiftFileDTO&gt;; view base fields via MapBaseFields; CopyEntity = generated property-by-property
/// copy. MapToList is an inline SQL-translatable projection (SelectWithTags for taggables).
///
/// Diagnostics: SHENGEN001 (not partial), SHENGEN002 (no mapper interface), SHENGEN003 (deep-mapping
/// cycle — member skipped), SHENGEN004 (unmapped view members — warning).
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
    private const string TaggableExtensions = "global::ShiftSoftware.ShiftEntity.EFCore.Tagging.TaggableProjectionExtensions";
    private const string AutoNamespace = "ShiftSoftware.ShiftEntity.GeneratedMappers";

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

    // ─────────────────────────────────── pipeline ───────────────────────────────────

    private sealed record DeclaredModel(INamedTypeSymbol Cls, string? Error, bool IsPair,
        ITypeSymbol? Entity, ITypeSymbol? ListDto, ITypeSymbol? ViewDto);

    private sealed record TripleModel(ITypeSymbol Entity, ITypeSymbol ListDto, ITypeSymbol ViewDto);

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

        var everything = declared.Combine(repoTriples).Combine(endpointTriples).Combine(configPairs).Combine(context.CompilationProvider);

        context.RegisterSourceOutput(everything, static (spc, data) =>
        {
            var ((((declaredModels, fromRepos), fromEndpoints), configSeeds), compilation) = data;
            GenerateAll(spc, declaredModels, fromRepos, fromEndpoints, configSeeds, compilation);
        });
    }

    private static bool IsDeepMappingMethod(string name) =>
        name is "ForListChildren" or "ForListChild" or "ForEntityChildren" or "ForEntityChild"
             or "ForViewChildren" or "ForViewChild";

    // Every deep-mapping builder method is <TChildEntity, TChildDto>(...) in that order — read the pair
    // straight off the (inference-resolved) call symbol. Guards keep it to genuine ShiftMapperBuilder calls.
    private static PairSeed? BuildConfigPair(GeneratorSyntaxContext ctx)
    {
        if (ctx.SemanticModel.GetSymbolInfo((InvocationExpressionSyntax)ctx.Node).Symbol is not IMethodSymbol method ||
            !IsDeepMappingMethod(method.Name) ||
            method.TypeArguments.Length != 2 ||
            method.ContainingType is not { Name: "ShiftMapperBuilder" } container ||
            !IsShiftNamespace(container))
            return null;

        var entity = method.TypeArguments[0];
        var dto = method.TypeArguments[1];

        return IsOpenOrError(entity) || IsOpenOrError(dto) ? null : new PairSeed(entity, dto);
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

            return new TripleModel(entity, listDto, viewDto);
        }

        return null;
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
                models.Add(new TripleModel(entity, listDto, viewDto));
        }

        return models.ToImmutable();
    }

    private static bool IsOpenOrError(ITypeSymbol t) => t is ITypeParameterSymbol || t.TypeKind == TypeKind.Error;

    // ─────────────────────────────────── the combined generation step ───────────────────────────────────

    private sealed class PairInfo
    {
        public ITypeSymbol Entity = null!;
        public ITypeSymbol Dto = null!;
        public INamedTypeSymbol? UserClass;
        public string ClassName = "";   // simple name (auto) — for declared pairs the user class name
        public string TypeRef = "";     // fully qualified reference used by consumers
    }

    private static void GenerateAll(SourceProductionContext spc,
        ImmutableArray<DeclaredModel> declaredModels,
        ImmutableArray<TripleModel> fromRepos,
        ImmutableArray<TripleModel> fromEndpoints,
        ImmutableArray<PairSeed> configSeeds,
        Compilation compilation)
    {
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

        void Discover(string ownerKey, string ownerDisplay, ITypeSymbol entity, ITypeSymbol viewDto)
        {
            var entityProps = AllProps(entity).Where(IsReadable).ToDictionary(p => p.Name, p => p);

            foreach (var dtoProp in AllProps(viewDto).Where(IsSettable))
            {
                if (ViewHandledMembers.Contains(dtoProp.Name) ||
                    ViewConvention(entityProps, dtoProp, compilation) is not null ||
                    !TryGetComposableChild(entityProps, dtoProp, out var childEntity, out var childDto, out _))
                    continue;

                var key = PairKey(childEntity, childDto);

                if (stack.Contains(key))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(MappingCycle, Location.None,
                        string.Join(" → ", stack.Concat(new[] { key }).Select(ShortPair)),
                        $"{ownerDisplay}.{dtoProp.Name}"));
                    skippedEdges.Add(ownerKey + "|" + dtoProp.Name);
                    continue;
                }

                if (pairs.ContainsKey(key))
                    continue;

                declaredPairs.TryGetValue(key, out var declaredPair);

                var info = new PairInfo { Entity = childEntity, Dto = childDto, UserClass = declaredPair?.Cls };

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
                Discover(key, ShortPair(key), childEntity, childDto);
                stack.RemoveAt(stack.Count - 1);
            }
        }

        foreach (var (key, (triple, _)) in triples.Select(kv => (kv.Key, kv.Value)))
        {
            stack.Clear();
            Discover(key, triple.Entity.Name, triple.Entity, triple.ViewDto);
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
            };

            stack.Clear();
            stack.Add(kv.Key);
            Discover(kv.Key, ShortPair(kv.Key), model.Entity!, model.ViewDto!);
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

            var info = new PairInfo { Entity = seed.Entity, Dto = seed.Dto, UserClass = declaredForSeed?.Cls };

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
            Discover(key, ShortPair(key), seed.Entity, seed.Dto);
        }

        // 4. Emit pair mappers.
        foreach (var (key, pair) in pairs.Select(kv => (kv.Key, kv.Value)))
            EmitPair(spc, key, pair, pairs, skippedEdges, compilation);

        // 5. Emit triple mappers.
        foreach (var (key, (triple, userClass)) in triples.Select(kv => (kv.Key, kv.Value)))
            EmitTriple(spc, key, triple, userClass, pairs, skippedEdges, compilation);
    }

    private static string TripleKey(ITypeSymbol e, ITypeSymbol l, ITypeSymbol v) => $"{Fq(e)}|{Fq(l)}|{Fq(v)}";
    private static string PairKey(ITypeSymbol e, ITypeSymbol d) => $"{Fq(e)}~{Fq(d)}";
    private static string ShortPair(string pairKey)
    {
        var parts = pairKey.Split('~');
        return $"({parts[0].Split('.').Last()}, {parts[1].Split('.').Last()})";
    }

    // ─────────────────────────────────── emission: shared infra ───────────────────────────────────

    private static readonly HashSet<string> ViewHandledMembers = new(StringComparer.Ordinal)
    { "ID", "IsDeleted", "CreateDate", "LastSaveDate", "CreatedByUserID", "LastSavedByUserID", "Tags", "Revisions" };

    private static readonly HashSet<string> EntityExcludedMembers = new(StringComparer.Ordinal)
    { "ID", "CreateDate", "LastSaveDate", "IsDeleted", "CreatedByUserID", "LastSavedByUserID", "ReloadAfterSave", "AuditFieldsAreSet", "IdempotencyKey", "Tags" };

    private static readonly HashSet<string> CopyExcludedMembers = new(StringComparer.Ordinal)
    { "ID", "ReloadAfterSave", "AuditFieldsAreSet" };

    private static StringBuilder StartClass(string? ns, string className, string declaration, string builderType, string configurableInterface, string entityName, string listName)
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
        sb.AppendLine($"    void {configurableInterface}.AddConfiguration(global::System.Action<{builderType}> configure)");
        sb.AppendLine("    {");
        sb.AppendLine("        configure(this.__ShiftMap);");
        sb.AppendLine("        this.__shiftComposedListProjection = null;");
        sb.AppendLine("    }");
        sb.AppendLine();

        return sb;
    }

    /// <summary>Convention RHS for a view-direction member, or null (accessor = the source parameter name).</summary>
    private static string? ViewConvention(Dictionary<string, IPropertySymbol> entityProps, IPropertySymbol dtoProp, Compilation compilation, string accessor = "entity")
    {
        if (IsShiftType(dtoProp.Type, "ShiftEntitySelectDTO"))
        {
            if (entityProps.TryGetValue(dtoProp.Name + "ID", out var fk) && (IsLong(fk.Type) || IsNullableLong(fk.Type)))
            {
                var text = entityProps.TryGetValue(dtoProp.Name, out var nav) && HasStringName(nav.Type)
                    ? $", {accessor}.{dtoProp.Name} != null ? {accessor}.{dtoProp.Name}.Name : null"
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

        var srcEnum = match.Type.TypeKind == TypeKind.Enum ? match.Type : UnwrapNullable(match.Type) is { TypeKind: TypeKind.Enum } se ? se : null;
        if (srcEnum is not null)
        {
            if (dtoProp.Type.SpecialType == SpecialType.System_Int32 && match.Type.TypeKind == TypeKind.Enum)
                return $"(int){accessor}.{dtoProp.Name}";
            if (UnwrapNullable(dtoProp.Type)?.SpecialType == SpecialType.System_Int32)
                return $"(int?){accessor}.{dtoProp.Name}";
        }

        return null;
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

        if (TryGetElement(dtoProp.Type, out var dtoElement) && TryGetElement(src.Type, out var entityElement))
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

    private sealed record ViewEmission(List<string> Lines, List<string> UsedPairKeys, List<string> Unmapped);

    private static ViewEmission BuildViewBody(string ownerKey, ITypeSymbol entity, ITypeSymbol viewDto,
        string entityName, string viewName, string accessor,
        Dictionary<string, PairInfo> pairs, HashSet<string> skippedEdges, Compilation compilation)
    {
        var entityProps = AllProps(entity).Where(IsReadable).ToDictionary(p => p.Name, p => p);
        var lines = new List<string>();
        var usedPairs = new List<string>();
        var unmapped = new List<string>();

        void Emit(IPropertySymbol dtoProp, bool withConvention)
        {
            var propType = Fq(dtoProp.Type);
            // Convention body written over the lambda parameters (e = entity/source, sp = context).
            string? conv = null;

            if (withConvention)
            {
                conv = ViewConvention(entityProps, dtoProp, compilation, "e");

                // Complex children are NOT auto-composed in the view direction — they compose only when
                // explicitly told via ForViewChild(ren) (same principle as list/entity: the programmer decides
                // how deep and in which direction). Such a member gets no convention (keeps its default until a
                // ForView(Child) sets it) and is NOT flagged unmapped. A SelectDTO stays a convention (leaf).
                if (conv is null && !TryGetComposableChild(entityProps, dtoProp, out _, out _, out _))
                    unmapped.Add(dtoProp.Name);
            }

            if (conv is not null)
            {
                // One reusable call: the ForView customization wins, else the convention lambda runs.
                lines.Add($"        dto.{dtoProp.Name} = this.__ShiftMap.ResolveView<{propType}>({accessor}, context, \"{dtoProp.Name}\", static (e, sp) => {conv});");
            }
            else
            {
                // Customization-only member (base/framework field or unmapped): the customization if present,
                // else keep the current value (what MapBaseFields set, or the DTO default). No if-guard.
                lines.Add($"        dto.{dtoProp.Name} = this.__ShiftMap.ResolveView<{propType}>({accessor}, context, \"{dtoProp.Name}\", dto.{dtoProp.Name});");
            }

            lines.Add("");
        }

        var settable = AllProps(viewDto).Where(IsSettable).ToList();

        foreach (var dtoProp in settable.Where(p => !ViewHandledMembers.Contains(p.Name)))
            Emit(dtoProp, withConvention: true);

        if (DerivesFrom(viewDto, "ShiftEntityViewAndUpsertDTO") && DerivesFrom(entity, "ShiftEntity"))
        {
            lines.Add($"        {Helpers}.MapBaseFields(dto, {accessor});");
            lines.Add("");
        }

        foreach (var dtoProp in settable.Where(p => ViewHandledMembers.Contains(p.Name)))
            Emit(dtoProp, withConvention: false);

        return new ViewEmission(lines, usedPairs, unmapped);
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

        return null;
    }

    private static List<string> BuildEntityBody(ITypeSymbol entity, ITypeSymbol viewDto, string entityName, string viewName, Compilation compilation)
    {
        var dtoProps = AllProps(viewDto).Where(IsReadable).ToDictionary(p => p.Name, p => p);
        var lines = new List<string>();

        void Emit(IPropertySymbol entityProp, bool withConvention)
        {
            var propType = Fq(entityProp.Type);
            var conv = withConvention ? EntityConvention(dtoProps, entityProp, compilation, "d") : null;

            if (conv is not null)
            {
                lines.Add($"        existing.{entityProp.Name} = this.__ShiftMap.ResolveEntity<{propType}>(dto, context, \"{entityProp.Name}\", static (d, sp) => {conv});");
            }
            else if (IsEntityNavigation(entityProp.Type))
            {
                // Navigation: apply the customization if present, but NEVER read existing.<nav>
                // (avoids triggering lazy loading) — so a guard, not the value-fallback overload.
                var cast = $"global::System.Func<{viewName}, global::ShiftSoftware.ShiftEntity.Core.MappingContext, {propType}>";
                lines.Add($"        if (this.__ShiftMap.TryGetEntityValue(\"{entityProp.Name}\", out var __e_{entityProp.Name}))");
                lines.Add($"            existing.{entityProp.Name} = (({cast})__e_{entityProp.Name})(dto, context);");
            }
            else
            {
                // Key/audit/excluded scalar: the customization if present, else keep the existing value.
                lines.Add($"        existing.{entityProp.Name} = this.__ShiftMap.ResolveEntity<{propType}>(dto, context, \"{entityProp.Name}\", existing.{entityProp.Name});");
            }

            lines.Add("");
        }

        var settable = AllProps(entity).Where(IsSettable).ToList();

        foreach (var entityProp in settable.Where(p => !EntityExcludedMembers.Contains(p.Name) && !IsEntityNavigation(p.Type)))
            Emit(entityProp, withConvention: true);

        foreach (var entityProp in settable.Where(p => EntityExcludedMembers.Contains(p.Name) || IsEntityNavigation(p.Type)))
            Emit(entityProp, withConvention: false);

        return lines;
    }

    private static List<string> BuildListAssignments(ITypeSymbol entity, ITypeSymbol listDto, Compilation compilation)
    {
        var entityProps = AllProps(entity).Where(IsReadable).ToDictionary(p => p.Name, p => p);
        var assignments = new List<string>();

        foreach (var dtoProp in AllProps(listDto).Where(p => IsSettable(p) && p.Name != "Tags"))
        {
            // FK → ShiftEntitySelectDTO, inlined so the projection stays SQL-translatable (the ToSelectDTO
            // helper is a method call EF can't translate; a member-init + navigation access it can). A
            // SelectDTO is a LEAF reference (id + name), so it maps by convention — unlike a complex child
            // object or a collection, which are composed only when explicitly told via ForListChild(ren).
            if (IsShiftType(dtoProp.Type, "ShiftEntitySelectDTO") &&
                entityProps.TryGetValue(dtoProp.Name + "ID", out var listFk) && (IsLong(listFk.Type) || IsNullableLong(listFk.Type)))
            {
                const string selectDto = "global::ShiftSoftware.ShiftEntity.Model.Dtos.ShiftEntitySelectDTO";
                var text = entityProps.TryGetValue(dtoProp.Name, out var listNav) && HasStringName(listNav.Type)
                    ? $"e.{dtoProp.Name} != null ? e.{dtoProp.Name}.Name : null"
                    : "null";

                assignments.Add(IsLong(listFk.Type)
                    ? $"            {dtoProp.Name} = new {selectDto} {{ Value = e.{dtoProp.Name}ID.ToString(), Text = {text} }},"
                    : $"            {dtoProp.Name} = e.{dtoProp.Name}ID == null ? null : new {selectDto} {{ Value = e.{dtoProp.Name}ID.Value.ToString(), Text = {text} }},");
                continue;
            }

            if (!entityProps.TryGetValue(dtoProp.Name, out var src))
                continue;

            if (IsImplicit(compilation, src.Type, dtoProp.Type))
            {
                assignments.Add($"            {dtoProp.Name} = e.{dtoProp.Name},");
                continue;
            }

            if (UnwrapNullable(src.Type) is { } narrowed && SymbolEqualityComparer.Default.Equals(narrowed, dtoProp.Type))
            {
                assignments.Add($"            {dtoProp.Name} = e.{dtoProp.Name} ?? default,");
                continue;
            }

            if (dtoProp.Type.SpecialType == SpecialType.System_String && IsLong(src.Type))
            {
                assignments.Add($"            {dtoProp.Name} = e.{dtoProp.Name}.ToString(),");
                continue;
            }

            if (dtoProp.Type.SpecialType == SpecialType.System_String && IsNullableLong(src.Type))
            {
                assignments.Add($"            {dtoProp.Name} = e.{dtoProp.Name}.HasValue ? e.{dtoProp.Name}.Value.ToString() : null,");
                continue;
            }

            var srcEnum = src.Type.TypeKind == TypeKind.Enum ? src.Type : UnwrapNullable(src.Type) is { TypeKind: TypeKind.Enum } se ? se : null;
            if (srcEnum is not null)
            {
                if (dtoProp.Type.SpecialType == SpecialType.System_Int32 && src.Type.TypeKind == TypeKind.Enum)
                    assignments.Add($"            {dtoProp.Name} = (int)e.{dtoProp.Name},");
                else if (UnwrapNullable(dtoProp.Type)?.SpecialType == SpecialType.System_Int32)
                    assignments.Add($"            {dtoProp.Name} = (int?)e.{dtoProp.Name},");
            }
        }

        return assignments;
    }

    private static List<string> BuildCopyBody(ITypeSymbol entity, string entityName)
    {
        var lines = new List<string>();

        void Emit(IPropertySymbol prop, bool withConvention)
        {
            var propType = Fq(prop.Type);

            if (withConvention)
            {
                lines.Add($"        target.{prop.Name} = this.__ShiftMap.ResolveCopy<{propType}>(source, context, \"{prop.Name}\", static (s, sp) => s.{prop.Name});");
            }
            else
            {
                // Excluded member (key/flags — all scalar): the customization if present, else keep target's value
                // (so e.g. ReloadAfterSave is preserved, never copied from source). No if-guard.
                lines.Add($"        target.{prop.Name} = this.__ShiftMap.ResolveCopy<{propType}>(source, context, \"{prop.Name}\", target.{prop.Name});");
            }

            lines.Add("");
        }

        var copyable = AllProps(entity).Where(p => IsReadable(p) && IsSettable(p)).ToList();

        foreach (var prop in copyable.Where(p => !CopyExcludedMembers.Contains(p.Name)))
            Emit(prop, withConvention: true);

        foreach (var prop in copyable.Where(p => CopyExcludedMembers.Contains(p.Name)))
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
        Dictionary<string, PairInfo> pairs, HashSet<string> skippedEdges, Compilation compilation)
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

        var sb = StartClass(ns, pair.ClassName, declaration, builderType, configurable, entityName, dtoName);

        var view = BuildViewBody(key, pair.Entity, pair.Dto, entityName, dtoName, "source", pairs, skippedEdges, compilation);
        AppendPairFields(sb, view.UsedPairKeys, pairs);

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
            sb.Append(string.Join("\n", BuildEntityBody(pair.Entity, pair.Dto, entityName, dtoName, compilation)));
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
        sb.AppendLine($"    public static readonly global::System.Linq.Expressions.Expression<global::System.Func<{entityName}, {dtoName}>> Projection = e => new {dtoName}");
        sb.AppendLine("    {");
        sb.Append(string.Join("\n", BuildListAssignments(pair.Entity, pair.Dto, compilation)));
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
                pair.UserClass?.Locations.FirstOrDefault() ?? Location.None,
                pair.ClassName, string.Join(", ", view.Unmapped)));

        spc.AddSource($"{pair.ClassName}.g.cs", sb.ToString());
    }

    // ─────────────────────────────────── emission: triple mappers ───────────────────────────────────

    private static void EmitTriple(SourceProductionContext spc, string key, TripleModel triple, INamedTypeSymbol? userClass,
        Dictionary<string, PairInfo> pairs, HashSet<string> skippedEdges, Compilation compilation)
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

        var sb = StartClass(ns, className, declaration, builderType, configurable, entityName, listName);

        var view = BuildViewBody(key, triple.Entity, triple.ViewDto, entityName, viewName, "entity", pairs, skippedEdges, compilation);
        AppendPairFields(sb, view.UsedPairKeys, pairs);

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
            sb.Append(string.Join("\n", BuildEntityBody(triple.Entity, triple.ViewDto, entityName, viewName, compilation)));
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

            sb.AppendLine($"    private static readonly global::System.Linq.Expressions.Expression<global::System.Func<{entityName}, {listName}>> __shiftListProjection = e => new {listName}");
            sb.AppendLine("    {");
            sb.Append(string.Join("\n", BuildListAssignments(triple.Entity, triple.ListDto, compilation)));
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
            sb.Append(string.Join("\n", BuildCopyBody(triple.Entity, entityName)));
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
                userClass?.Locations.FirstOrDefault() ?? Location.None,
                className, string.Join(", ", view.Unmapped)));

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

    private static bool HasStringName(ITypeSymbol type) =>
        AllProps(type).Any(p => p.Name == "Name" && p.Type.SpecialType == SpecialType.System_String && IsReadable(p));

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
