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
/// Generates convention-based IShiftEntityMapper implementations and registers them (via module
/// initializers) in ShiftEntityMapperRegistry, keyed by the (entity, list DTO, view DTO) triple.
///
/// Two sources:
///  1. AUTO-DISCOVERY (zero programmer code): every triple found in the compilation — from
///     ShiftRepository&lt;DB, TEntity, TList, TView&gt; subclasses and from [ShiftEntityEndpoint*]
///     attributes on entities — gets an auto-named internal mapper. These are what
///     options.UseGeneratedMapper() and endpoint attributes with UseGeneratedMapper = true resolve.
///  2. CUSTOMIZATION: a programmer-declared [ShiftEntityMapper] partial class implementing
///     IShiftEntityMapper&lt;,,&gt;. The generator fills only the methods the programmer didn't write
///     (user-implemented wins) and registers THAT class for the triple instead of an auto one.
///
/// Conventions (the "simple cases"):
///  - Scalars map by name when an implicit conversion exists.
///  - DTO ShiftEntitySelectDTO property X ↔ entity FK "XID" (long/long?) via MappingHelpers.ToSelectDTO /
///    ToForeignKey / ToNullableForeignKey; the optional nav property X supplies the display text.
///  - DTO List&lt;ShiftFileDTO&gt; ↔ entity string via ToShiftFiles / ToJsonString.
///  - View DTO audit/base fields via MapBaseFields; entity base/infrastructure fields are never written.
///  - MapToList is an inline, SQL-translatable Select projection (long→string, enum→int casts inlined;
///    SelectWithTags for taggable entities). Unmatched members are skipped.
///  - CopyEntity is a generated property-by-property copy (same contract as ShallowCopyTo, without the
///    reflection): every public read/write property including navigations, except ID, ReloadAfterSave,
///    and AuditFieldsAreSet.
/// </summary>
[Generator]
public sealed class ShiftEntityMapperGenerator : IIncrementalGenerator
{
    private const string AttributeFullName = "ShiftSoftware.ShiftEntity.Core.ShiftEntityMapperAttribute";
    private const string Helpers = "global::ShiftSoftware.ShiftEntity.Core.MappingHelpers";
    private const string TaggableExtensions = "global::ShiftSoftware.ShiftEntity.EFCore.Tagging.TaggableProjectionExtensions";
    private const string AutoNamespace = "ShiftSoftware.ShiftEntity.GeneratedMappers";

    private static readonly DiagnosticDescriptor NotPartial = new(
        id: "SHENGEN001",
        title: "Mapper class must be partial",
        messageFormat: "Class '{0}' is marked [ShiftEntityMapper] but is not declared partial; the generator cannot add the mapping methods",
        category: "ShiftEntity.Mapping",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoMapperInterface = new(
        id: "SHENGEN002",
        title: "Mapper class must implement IShiftEntityMapper<TEntity, TListDTO, TViewDTO>",
        messageFormat: "Class '{0}' is marked [ShiftEntityMapper] but does not implement IShiftEntityMapper<TEntity, TListDTO, TViewDTO>",
        category: "ShiftEntity.Mapping",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 1. Programmer-declared [ShiftEntityMapper] partial classes (the customization path).
        var declared = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeFullName,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, _) => BuildDeclared(ctx));

        context.RegisterSourceOutput(declared, static (spc, model) =>
        {
            if (model.Error is not null)
                spc.ReportDiagnostic(Diagnostic.Create(
                    model.Error == "partial" ? NotPartial : NoMapperInterface, Location.None, model.ClassName));

            if (model.Source is not null && model.HintName is not null)
                spc.AddSource(model.HintName, model.Source);
        });

        // 2. Auto-discovery: triples from ShiftRepository<DB, TEntity, TList, TView> subclasses.
        var repoTriples = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, _) => BuildFromRepository(ctx))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        // 3. Auto-discovery: triples from [ShiftEntityEndpoint*<TList, TView, …>] attributes on entities.
        var endpointTriples = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                static (ctx, _) => BuildFromEndpointAttributes(ctx))
            .SelectMany(static (models, _) => models);

        // Auto mappers are emitted only for triples WITHOUT a programmer-declared mapper class
        // (the declared class is registered for the triple instead — customization wins).
        var declaredKeys = declared
            .Where(static m => m.TripleKey is not null)
            .Select(static (m, _) => m.TripleKey!)
            .Collect();

        var autoMappers = repoTriples.Collect()
            .Combine(endpointTriples.Collect())
            .Combine(declaredKeys);

        context.RegisterSourceOutput(autoMappers, static (spc, data) =>
        {
            var ((fromRepos, fromEndpoints), declaredTriples) = data;
            var declaredSet = new HashSet<string>(declaredTriples, StringComparer.Ordinal);
            var emitted = new HashSet<string>(StringComparer.Ordinal);

            foreach (var model in fromRepos.Concat(fromEndpoints))
            {
                if (declaredSet.Contains(model.TripleKey!) || !emitted.Add(model.TripleKey!))
                    continue;

                spc.AddSource(model.HintName!, model.Source!);
            }
        });
    }

    private sealed record MapperModel(string? HintName, string? Source, string? Error, string ClassName, string? TripleKey);

    // ─────────────────────────────────── discovery ───────────────────────────────────

    private static MapperModel BuildDeclared(GeneratorAttributeSyntaxContext ctx)
    {
        var cls = (INamedTypeSymbol)ctx.TargetSymbol;

        var isPartial = cls.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<ClassDeclarationSyntax>()
            .Any(s => s.Modifiers.Any(SyntaxKind.PartialKeyword));

        if (!isPartial)
            return new MapperModel(null, null, "partial", cls.Name, null);

        var mapperInterface = cls.AllInterfaces.FirstOrDefault(i =>
            i.Name == "IShiftEntityMapper" &&
            i.TypeArguments.Length == 3 &&
            i.ContainingNamespace.ToDisplayString().StartsWith("ShiftSoftware.ShiftEntity", StringComparison.Ordinal));

        if (mapperInterface is null)
            return new MapperModel(null, null, "interface", cls.Name, null);

        var entity = mapperInterface.TypeArguments[0];
        var listDto = mapperInterface.TypeArguments[1];
        var viewDto = mapperInterface.TypeArguments[2];

        var ns = cls.ContainingNamespace.IsGlobalNamespace ? null : cls.ContainingNamespace.ToDisplayString();
        var source = Emit(ns, cls.Name, declareInterface: false, cls, entity, listDto, viewDto, ctx.SemanticModel.Compilation);
        var hintName = $"{(ns ?? "global").Replace('.', '_')}_{cls.Name}.g.cs";

        return new MapperModel(hintName, source, null, cls.Name, TripleKey(entity, listDto, viewDto));
    }

    private static MapperModel? BuildFromRepository(GeneratorSyntaxContext ctx)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node) is not INamedTypeSymbol cls)
            return null;

        for (var current = cls.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name != "ShiftRepository" ||
                current.TypeArguments.Length != 4 ||
                !current.ContainingNamespace.ToDisplayString().StartsWith("ShiftSoftware.ShiftEntity", StringComparison.Ordinal))
                continue;

            // ShiftRepository<DB, TEntity, TListDTO, TViewAndUpsertDTO>
            var entity = current.TypeArguments[1];
            var listDto = current.TypeArguments[2];
            var viewDto = current.TypeArguments[3];

            return BuildAuto(entity, listDto, viewDto, ctx.SemanticModel.Compilation);
        }

        return null;
    }

    private static ImmutableArray<MapperModel> BuildFromEndpointAttributes(GeneratorSyntaxContext ctx, System.Threading.CancellationToken _ = default)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node) is not INamedTypeSymbol entity ||
            !DerivesFrom(entity, "ShiftEntity"))
            return ImmutableArray<MapperModel>.Empty;

        var models = ImmutableArray.CreateBuilder<MapperModel>();

        foreach (var attribute in entity.GetAttributes())
        {
            var attrClass = attribute.AttributeClass;

            // All endpoint attribute variants declare TListDTO, TViewDTO as their first two type arguments.
            if (attrClass is null ||
                !attrClass.Name.StartsWith("ShiftEntity", StringComparison.Ordinal) ||
                !attrClass.Name.Contains("Endpoint") ||
                attrClass.TypeArguments.Length < 2 ||
                !attrClass.ContainingNamespace.ToDisplayString().StartsWith("ShiftSoftware.ShiftEntity", StringComparison.Ordinal))
                continue;

            var model = BuildAuto(entity, attrClass.TypeArguments[0], attrClass.TypeArguments[1], ctx.SemanticModel.Compilation);
            if (model is not null)
                models.Add(model);
        }

        return models.ToImmutable();
    }

    private static MapperModel? BuildAuto(ITypeSymbol entity, ITypeSymbol listDto, ITypeSymbol viewDto, Compilation compilation)
    {
        // Open generic bases (e.g. RepositoryBase<TEntity, TList, TView>) carry type parameters — skip;
        // the concrete subclasses produce the closed triples.
        if (entity is ITypeParameterSymbol || listDto is ITypeParameterSymbol || viewDto is ITypeParameterSymbol ||
            entity.TypeKind == TypeKind.Error || listDto.TypeKind == TypeKind.Error || viewDto.TypeKind == TypeKind.Error)
            return null;

        var key = TripleKey(entity, listDto, viewDto);
        var className = $"Generated_{entity.Name}_{listDto.Name}_{viewDto.Name}_{Fnv8(key)}";
        var source = Emit(AutoNamespace, className, declareInterface: true, userClass: null, entity, listDto, viewDto, compilation);

        return new MapperModel($"{className}.g.cs", source, null, className, key);
    }

    private static string TripleKey(ITypeSymbol entity, ITypeSymbol listDto, ITypeSymbol viewDto)
        => $"{Fq(entity)}|{Fq(listDto)}|{Fq(viewDto)}";

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

    // ─────────────────────────────────── emission ───────────────────────────────────

    private static string Emit(
        string? ns, string className, bool declareInterface, INamedTypeSymbol? userClass,
        ITypeSymbol entity, ITypeSymbol listDto, ITypeSymbol viewDto, Compilation compilation)
    {
        var entityName = Fq(entity);
        var listName = Fq(listDto);
        var viewName = Fq(viewDto);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated by ShiftSoftware.ShiftEntity.SourceGenerator — convention-based IShiftEntityMapper implementation.");
        sb.AppendLine("#nullable disable");
        sb.AppendLine();

        if (ns is not null)
        {
            sb.Append("namespace ").Append(ns).AppendLine(";");
            sb.AppendLine();
        }

        sb.Append(declareInterface ? "internal sealed class " : "partial class ").Append(className);
        if (declareInterface)
            sb.Append($" : global::ShiftSoftware.ShiftEntity.Core.IShiftEntityMapper<{entityName}, {listName}, {viewName}>");
        sb.AppendLine();
        sb.AppendLine("{");

        var emitted = new List<string>();

        if (userClass is null || !HasUserMethod(userClass, "MapToView"))
            emitted.Add(EmitMapToView(entity, viewDto, entityName, viewName, compilation));

        if (userClass is null || !HasUserMethod(userClass, "MapToEntity"))
            emitted.Add(EmitMapToEntity(entity, viewDto, entityName, viewName, compilation));

        if (userClass is null || !HasUserMethod(userClass, "MapToList"))
            emitted.Add(EmitMapToList(entity, listDto, entityName, listName, compilation));

        if (userClass is null || !HasUserMethod(userClass, "CopyEntity"))
            emitted.Add(EmitCopyEntity(entity, entityName));

        sb.Append(string.Join("\n", emitted));
        sb.AppendLine("}");

        // Register the mapper in the runtime registry (keyed by the triple) at module load, so
        // options.UseGeneratedMapper() and endpoint attributes with UseGeneratedMapper = true can find it
        // without any DI registration.
        var mapperTypeName = ns is null ? className : $"global::{ns}.{className}";
        sb.AppendLine();
        sb.AppendLine($"internal static class {className}Registration");
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");
        sb.AppendLine("        global::ShiftSoftware.ShiftEntity.Core.ShiftEntityMapperRegistry.Register(");
        sb.AppendLine($"            typeof({entityName}), typeof({listName}), typeof({viewName}), typeof({mapperTypeName}));");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string EmitMapToView(ITypeSymbol entity, ITypeSymbol viewDto, string entityName, string viewName, Compilation compilation)
    {
        // Base fields are handled by MapBaseFields; Tags by the framework's tagging pipeline.
        var handled = new HashSet<string>(StringComparer.Ordinal)
        { "ID", "IsDeleted", "CreateDate", "LastSaveDate", "CreatedByUserID", "LastSavedByUserID", "Tags" };

        var entityProps = AllProps(entity).Where(p => IsReadable(p)).ToDictionary(p => p.Name, p => p);

        var assignments = new List<string>();

        foreach (var dtoProp in AllProps(viewDto).Where(p => IsSettable(p) && !handled.Contains(p.Name)))
        {
            // FK → ShiftEntitySelectDTO (nav property supplies the text when present).
            if (IsShiftType(dtoProp.Type, "ShiftEntitySelectDTO"))
            {
                if (entityProps.TryGetValue(dtoProp.Name + "ID", out var fk) && (IsLong(fk.Type) || IsNullableLong(fk.Type)))
                {
                    var text = entityProps.TryGetValue(dtoProp.Name, out var nav) && HasStringName(nav.Type)
                        ? $", entity.{dtoProp.Name} != null ? entity.{dtoProp.Name}.Name : null"
                        : "";
                    assignments.Add($"            {dtoProp.Name} = {Helpers}.ToSelectDTO(entity.{dtoProp.Name}ID{text}),");
                }
                continue;
            }

            // string (JSON) → List<ShiftFileDTO>
            if (IsShiftFileList(dtoProp.Type))
            {
                if (entityProps.TryGetValue(dtoProp.Name, out var src) && src.Type.SpecialType == SpecialType.System_String)
                    assignments.Add($"            {dtoProp.Name} = {Helpers}.ToShiftFiles(entity.{dtoProp.Name}),");
                continue;
            }

            if (!entityProps.TryGetValue(dtoProp.Name, out var match))
                continue;

            // Name + implicitly-convertible type match.
            if (IsImplicit(compilation, match.Type, dtoProp.Type))
            {
                assignments.Add($"            {dtoProp.Name} = entity.{dtoProp.Name},");
                continue;
            }

            // entity T? → DTO T (value types): narrow with default fallback (mirrors MapToEntity's reverse rule).
            if (UnwrapNullable(match.Type) is { } narrowed && SymbolEqualityComparer.Default.Equals(narrowed, dtoProp.Type))
                assignments.Add($"            {dtoProp.Name} = entity.{dtoProp.Name} ?? default,");
        }

        var mapBase = DerivesFrom(viewDto, "ShiftEntityViewAndUpsertDTO") && DerivesFrom(entity, "ShiftEntity")
            ? $"        return {Helpers}.MapBaseFields(dto, entity);"
            : "        return dto;";

        return
$@"    public {viewName} MapToView({entityName} entity, global::System.IServiceProvider serviceProvider = null)
    {{
        var dto = new {viewName}
        {{
{string.Join("\n", assignments)}
        }};

{mapBase}
    }}
";
    }

    private static string EmitMapToEntity(ITypeSymbol entity, ITypeSymbol viewDto, string entityName, string viewName, Compilation compilation)
    {
        // Never written by the mapper: key, audit fields, framework-managed state, tags.
        var excluded = new HashSet<string>(StringComparer.Ordinal)
        { "ID", "CreateDate", "LastSaveDate", "IsDeleted", "CreatedByUserID", "LastSavedByUserID", "ReloadAfterSave", "AuditFieldsAreSet", "IdempotencyKey", "Tags" };

        var dtoProps = AllProps(viewDto).Where(p => IsReadable(p)).ToDictionary(p => p.Name, p => p);

        var lines = new List<string>();

        foreach (var entityProp in AllProps(entity).Where(p => IsSettable(p) && !excluded.Contains(p.Name)))
        {
            // Navigation properties / entity collections are never written.
            if (IsEntityNavigation(entityProp.Type))
                continue;

            // FK "XID" ← DTO ShiftEntitySelectDTO "X"
            if (entityProp.Name.EndsWith("ID", StringComparison.Ordinal) && entityProp.Name.Length > 2)
            {
                var baseName = entityProp.Name.Substring(0, entityProp.Name.Length - 2);
                if (dtoProps.TryGetValue(baseName, out var select) && IsShiftType(select.Type, "ShiftEntitySelectDTO"))
                {
                    if (IsLong(entityProp.Type))
                    {
                        lines.Add($"        existing.{entityProp.Name} = {Helpers}.ToForeignKey(dto.{baseName});");
                        continue;
                    }

                    if (IsNullableLong(entityProp.Type))
                    {
                        lines.Add($"        existing.{entityProp.Name} = {Helpers}.ToNullableForeignKey(dto.{baseName});");
                        continue;
                    }
                }
            }

            if (!dtoProps.TryGetValue(entityProp.Name, out var dtoProp))
                continue;

            // List<ShiftFileDTO> → string (JSON)
            if (entityProp.Type.SpecialType == SpecialType.System_String && IsShiftFileList(dtoProp.Type))
            {
                lines.Add($"        existing.{entityProp.Name} = {Helpers}.ToJsonString(dto.{entityProp.Name});");
                continue;
            }

            // Implicitly-convertible name match.
            if (IsImplicit(compilation, dtoProp.Type, entityProp.Type))
            {
                lines.Add($"        existing.{entityProp.Name} = dto.{entityProp.Name};");
                continue;
            }

            // DTO T? → entity T (value types): fall back to default when the DTO carries null.
            if (UnwrapNullable(dtoProp.Type) is { } inner && SymbolEqualityComparer.Default.Equals(inner, entityProp.Type))
                lines.Add($"        existing.{entityProp.Name} = dto.{entityProp.Name} ?? default;");
        }

        return
$@"    public {entityName} MapToEntity({viewName} dto, {entityName} existing, global::System.IServiceProvider serviceProvider = null)
    {{
{string.Join("\n", lines)}

        return existing;
    }}
";
    }

    private static string EmitMapToList(ITypeSymbol entity, ITypeSymbol listDto, string entityName, string listName, Compilation compilation)
    {
        var entityProps = AllProps(entity).Where(p => IsReadable(p)).ToDictionary(p => p.Name, p => p);

        var assignments = new List<string>();

        foreach (var dtoProp in AllProps(listDto).Where(p => IsSettable(p) && p.Name != "Tags"))
        {
            if (!entityProps.TryGetValue(dtoProp.Name, out var src))
                continue;

            // Implicitly-convertible name match (covers identity, T→T?, enum→enum?).
            if (IsImplicit(compilation, src.Type, dtoProp.Type))
            {
                assignments.Add($"            {dtoProp.Name} = e.{dtoProp.Name},");
                continue;
            }

            // entity T? → DTO T (value types): narrow with default fallback (translates to COALESCE).
            if (UnwrapNullable(src.Type) is { } narrowed && SymbolEqualityComparer.Default.Equals(narrowed, dtoProp.Type))
            {
                assignments.Add($"            {dtoProp.Name} = e.{dtoProp.Name} ?? default,");
                continue;
            }

            // long / long? → string (IDs and FKs on list DTOs).
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

            // enum → int / enum? → int?
            var srcEnum = src.Type.TypeKind == TypeKind.Enum ? src.Type : UnwrapNullable(src.Type) is { TypeKind: TypeKind.Enum } se ? se : null;
            if (srcEnum is not null)
            {
                if (dtoProp.Type.SpecialType == SpecialType.System_Int32 && src.Type.TypeKind == TypeKind.Enum)
                    assignments.Add($"            {dtoProp.Name} = (int)e.{dtoProp.Name},");
                else if (UnwrapNullable(dtoProp.Type)?.SpecialType == SpecialType.System_Int32)
                    assignments.Add($"            {dtoProp.Name} = (int?)e.{dtoProp.Name},");
            }
        }

        // Taggable entity + taggable list DTO → SelectWithTags appends the canonical Tags projection.
        var taggable =
            entity.AllInterfaces.Any(i => i.Name == "IShiftEntityTaggable") &&
            listDto.AllInterfaces.Any(i => i.Name == "IShiftEntityTaggableDTO");

        var select = taggable
            ? $"{TaggableExtensions}.SelectWithTags"
            : "global::System.Linq.Queryable.Select";

        return
$@"    public global::System.Linq.IQueryable<{listName}> MapToList(global::System.Linq.IQueryable<{entityName}> queryable, global::System.IServiceProvider serviceProvider = null)
    {{
        return {select}(queryable, e => new {listName}
        {{
{string.Join("\n", assignments)}
        }});
    }}
";
    }

    private static string EmitCopyEntity(ITypeSymbol entity, string entityName)
    {
        // Same contract as MappingHelpers.ShallowCopyTo, but as generated assignments (no reflection):
        // copy every public read/write property — navigations included, since CopyEntity's job is to
        // bring freshly-loaded state onto the tracked instance — except the key and framework flags.
        var excluded = new HashSet<string>(StringComparer.Ordinal)
        { "ID", "ReloadAfterSave", "AuditFieldsAreSet" };

        var lines = AllProps(entity)
            .Where(p => IsReadable(p) && IsSettable(p) && !excluded.Contains(p.Name))
            .Select(p => $"        target.{p.Name} = source.{p.Name};");

        return
$@"    public void CopyEntity({entityName} source, {entityName} target, global::System.IServiceProvider serviceProvider = null)
    {{
{string.Join("\n", lines)}
    }}
";
    }

    // ─────────────────────────────────── symbol helpers ───────────────────────────────────

    private static string Fq(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

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
        type is INamedTypeSymbol named &&
        named.Name == name &&
        named.ContainingNamespace.ToDisplayString().StartsWith("ShiftSoftware.ShiftEntity", StringComparison.Ordinal);

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
            if (current.Name == baseName &&
                current.ContainingNamespace.ToDisplayString().StartsWith("ShiftSoftware.ShiftEntity", StringComparison.Ordinal))
                return true;

        return false;
    }

    private static bool HasStringName(ITypeSymbol type) =>
        AllProps(type).Any(p => p.Name == "Name" && p.Type.SpecialType == SpecialType.System_String && IsReadable(p));

    private static bool IsEntityNavigation(ITypeSymbol type)
    {
        if (DerivesFromShiftEntityBase(type))
            return true;

        // Collections of entities (ICollection<T>, List<T>, IEnumerable<T>, ...).
        return type is INamedTypeSymbol { TypeArguments.Length: 1 } named && DerivesFromShiftEntityBase(named.TypeArguments[0]);
    }

    private static bool DerivesFromShiftEntityBase(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.Name == "ShiftEntityBase" &&
                current.ContainingNamespace.ToDisplayString().StartsWith("ShiftSoftware.ShiftEntity", StringComparison.Ordinal))
                return true;

        return false;
    }
}
