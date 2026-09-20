using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Analyzers;

/// <summary>
/// Reports the one repository-configuration mistake nothing at run time can: an entity declaring
/// <c>IConfiguresShiftRepository&lt;E, L, V&gt;</c> while a repository for the SAME triple passes an options builder
/// to its base constructor. Passing a builder means the repository configures itself and takes over completely,
/// so the entity's <c>ConfigureRepository</c> never runs — both halves look correct in isolation and nothing fails,
/// the configuration just never applies. That is why it is a build ERROR (<see cref="EntityConfigurationSuppressedId"/>)
/// rather than a warning: the failure is invisible at run time and the fix (move the configuration into the
/// builder, or drop the builder) is always available.
/// <para>
/// Because it breaks the build it may only fire on a certainty. It is answered only for a class whose DIRECT base
/// is <c>ShiftRepository&lt;,,,&gt;</c>, reading the very <c>base(...)</c> call that reaches it; through an
/// intermediate class whether a builder arrives is a run-time value, so it stays silent. And EVERY constructor
/// reaching base must pass a builder — one that does not means the repository can also be built the
/// entity-configured way, so the suppression is a maybe. There is no opt-out attribute.
/// </para>
/// <para>
/// This was SHENGEN006 of the retired ShiftEntity source generator (the mapping moved to ShiftMapper — see
/// <c>docs/plans/repository-mapping-on-shiftmapper</c> in ShiftTemplates); the rule is unchanged, only re-homed.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RepositoryConfigurationAnalyzer : DiagnosticAnalyzer
{
    public const string EntityConfigurationSuppressedId = "SHENT001";

    public static readonly DiagnosticDescriptor EntityConfigurationSuppressed = new(
        EntityConfigurationSuppressedId,
        "Entity repository configuration will not run",
        "'{0}' declares IConfiguresShiftRepository<{0}, {1}, {2}>, but repository '{3}' configures the same triple by " +
        "passing an options builder to its base constructor. Passing a builder means the repository configures itself " +
        "and takes over completely, so '{0}'.ConfigureRepository will NOT run — move that configuration into '{3}'s " +
        "builder, or drop the builder to let the entity's configuration apply.",
        "ShiftEntity",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(EntityConfigurationSuppressed);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        var cls = (INamedTypeSymbol)context.Symbol;

        if (cls.TypeKind != TypeKind.Class || cls.BaseType is not { } baseType || !IsShiftRepository(baseType))
            return;

        var entity = baseType.TypeArguments[1];
        var listDto = baseType.TypeArguments[2];
        var viewDto = baseType.TypeArguments[3];

        // An open triple (a project's own generic RepositoryBase<TEntity, TDto>) names no entity to check.
        if (IsOpenOrError(entity) || IsOpenOrError(listDto) || IsOpenOrError(viewDto))
            return;

        if (!PassesOptionsBuilder(cls, context.CancellationToken) || !EntityConfiguresTriple(entity, listDto, viewDto))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            EntityConfigurationSuppressed, cls.Locations.FirstOrDefault(),
            entity.Name, listDto.Name, viewDto.Name, cls.Name));
    }

    /// <summary>
    /// Whether the repository hands an options builder to
    /// <c>ShiftRepository(DB db, Action&lt;ShiftRepositoryOptions&lt;…&gt;&gt;? shiftRepositoryBuilder = null)</c> from
    /// EVERY constructor that reaches base. Constructors chaining through <c>: this(...)</c> are not base calls; the
    /// one they land on is visited on its own. An explicit <c>base(db, null)</c> is "no builder".
    /// </summary>
    private static bool PassesOptionsBuilder(INamedTypeSymbol cls, System.Threading.CancellationToken cancellationToken)
    {
        var reachesBase = false;

        foreach (var ctor in cls.InstanceConstructors)
        {
            foreach (var syntaxRef in ctor.DeclaringSyntaxReferences)
            {
                if (syntaxRef.GetSyntax(cancellationToken) is not ConstructorDeclarationSyntax decl)
                    continue;

                if (decl.Initializer is { } chain && chain.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword))
                    continue;

                reachesBase = true;

                // base(db) => no builder. base(db, x => …) / base(db, SomeOptions) => configures itself.
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

    private static bool IsShiftRepository(INamedTypeSymbol type) =>
        type.Name == "ShiftRepository" && type.TypeArguments.Length == 4 && IsShiftNamespace(type);

    private static bool IsShiftNamespace(INamedTypeSymbol symbol) =>
        symbol.ContainingNamespace.ToDisplayString().StartsWith("ShiftSoftware.ShiftEntity", StringComparison.Ordinal);

    private static bool IsOpenOrError(ITypeSymbol t) => t is ITypeParameterSymbol || t.TypeKind == TypeKind.Error;
}
