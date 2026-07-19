using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Drives <see cref="SourceGenerator.ShiftEntityMapperGenerator"/> over a hand-built compilation and pins the
/// diagnostics it reports. The generator is referenced as a plain library here (never as this project's analyzer),
/// so these tests read its output directly rather than inferring it from a build.
/// <para>
/// Currently covers SHENGEN006 — an entity declaring <c>IConfiguresShiftRepository&lt;E, L, V&gt;</c> while a
/// repository for the SAME triple passes an options builder to its base constructor. The builder means the
/// repository configures itself and takes over, so the entity's <c>ConfigureRepository</c> never runs; nothing
/// fails at runtime, which is why this is a build ERROR. Because it breaks the build it must only fire on a
/// certainty — the "silent" cases below are as much the contract as the firing one.
/// </para>
/// </summary>
public class GeneratorDiagnosticTests
{
    private const string EntityConfigSuppressed = "SHENGEN006";

    /// <summary>
    /// The fixed half of every case: an entity configuring the (Widget, WidgetDTO, WidgetDTO) triple, plus a
    /// second DTO so a repository can sit on a DIFFERENT triple. <c>{{REPO}}</c> is the half under test.
    /// </summary>
    private const string Scaffold = """
        using System;
        using ShiftSoftware.ShiftEntity.Core;
        using ShiftSoftware.ShiftEntity.EFCore;
        using ShiftSoftware.ShiftEntity.Model.Dtos;

        namespace Sample;

        public class SampleDb : ShiftDbContext { }

        public class WidgetDTO : ShiftEntityDTOBase
        {
            public override string? ID { get; set; }
            public string Name { get; set; } = "";
        }

        public class OtherDTO : ShiftEntityDTOBase
        {
            public override string? ID { get; set; }
            public string Name { get; set; } = "";
        }

        public class Widget : ShiftEntity<Widget>, IConfiguresShiftRepository<Widget, WidgetDTO, WidgetDTO>
        {
            public string Name { get; set; } = "";

            public void ConfigureRepository(ShiftRepositoryConfigurationContext<Widget, WidgetDTO, WidgetDTO> context) { }
        }

        public class Plain : ShiftEntity<Plain>
        {
            public string Name { get; set; } = "";
        }

        {{REPO}}
        """;

    [Fact]
    public void RepoPassesBuilder_ForTheEntitysTriple_IsBuildError()
    {
        var diagnostic = Assert.Single(Run("""
            public class WidgetRepository : ShiftRepository<SampleDb, Widget, WidgetDTO, WidgetDTO>
            {
                public WidgetRepository(SampleDb db) : base(db, x => { }) { }
            }
            """), d => d.Id == EntityConfigSuppressed);

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);

        // Both halves must be nameable from the message — it is the only place the programmer sees them together.
        var message = diagnostic.GetMessage();
        Assert.Contains("Widget", message);
        Assert.Contains("WidgetRepository", message);
    }

    /// <summary>No builder is the entity-configured path working as intended — the whole point of the hook.</summary>
    [Fact]
    public void RepoPassesNoBuilder_IsSilent() => AssertSilent("""
        public class WidgetRepository : ShiftRepository<SampleDb, Widget, WidgetDTO, WidgetDTO>
        {
            public WidgetRepository(SampleDb db) : base(db) { }
        }
        """);

    /// <summary>An explicit null builder is "give me the default", so the entity's configuration still runs.</summary>
    [Fact]
    public void RepoPassesExplicitNullBuilder_IsSilent() => AssertSilent("""
        public class WidgetRepository : ShiftRepository<SampleDb, Widget, WidgetDTO, WidgetDTO>
        {
            public WidgetRepository(SampleDb db) : base(db, null) { }
        }
        """);

    /// <summary>
    /// The CountryRepository demo's shape: a self-configuring repository on its OWN DTO triple. Mappers and the
    /// entity hooks are keyed by the triple, so nothing of the entity's is suppressed.
    /// </summary>
    [Fact]
    public void RepoPassesBuilder_ForADifferentTriple_IsSilent() => AssertSilent("""
        public class WidgetRepository : ShiftRepository<SampleDb, Widget, OtherDTO, OtherDTO>
        {
            public WidgetRepository(SampleDb db) : base(db, x => { }) { }
        }
        """);

    /// <summary>A self-configuring repository is only a problem when an entity's configuration loses to it.</summary>
    [Fact]
    public void RepoPassesBuilder_ButEntityDoesNotConfigure_IsSilent() => AssertSilent("""
        public class PlainRepository : ShiftRepository<SampleDb, Plain, WidgetDTO, WidgetDTO>
        {
            public PlainRepository(SampleDb db) : base(db, x => { }) { }
        }
        """);

    /// <summary>
    /// One constructor reaching base WITHOUT a builder means the repository can also be built the
    /// entity-configured way — the suppression is a maybe, and an error may not fire on a maybe.
    /// </summary>
    [Fact]
    public void RepoHasOneCtorWithoutABuilder_IsSilent() => AssertSilent("""
        public class WidgetRepository : ShiftRepository<SampleDb, Widget, WidgetDTO, WidgetDTO>
        {
            public WidgetRepository(SampleDb db) : base(db) { }
            public WidgetRepository(SampleDb db, bool configure) : base(db, x => { }) { }
        }
        """);

    /// <summary>
    /// A `: this(...)` chain isn't a base call. Every constructor that does reach base passes a builder here, so
    /// the repository configures itself however it is constructed.
    /// </summary>
    [Fact]
    public void RepoChainsThroughThisToABuilderCtor_IsBuildError()
    {
        var diagnostic = Assert.Single(Run("""
            public class WidgetRepository : ShiftRepository<SampleDb, Widget, WidgetDTO, WidgetDTO>
            {
                public WidgetRepository(SampleDb db) : this(db, true) { }
                public WidgetRepository(SampleDb db, bool flag) : base(db, x => { }) { }
            }
            """), d => d.Id == EntityConfigSuppressed);

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    /// <summary>
    /// Through an intermediate base class the builder is a runtime value the generator can't read, so it stays
    /// silent rather than guessing — a false error here would be unsuppressible.
    /// </summary>
    [Fact]
    public void RepoInheritsThroughAnIntermediateBase_IsSilent() => AssertSilent("""
        public abstract class RepositoryBase<TEntity, TDto> : ShiftRepository<SampleDb, TEntity, TDto, TDto>
            where TEntity : ShiftEntity<TEntity>, new()
            where TDto : ShiftEntityDTOBase
        {
            protected RepositoryBase(SampleDb db, Action<ShiftRepositoryOptions<TEntity, TDto, TDto>>? builder) : base(db, builder) { }
        }

        public class WidgetRepository : RepositoryBase<Widget, WidgetDTO>
        {
            public WidgetRepository(SampleDb db) : base(db, x => { }) { }
        }
        """);

    // ──────────────────────────────── harness ────────────────────────────────

    private static void AssertSilent(string repository) =>
        Assert.DoesNotContain(Run(repository), d => d.Id == EntityConfigSuppressed);

    private static ImmutableArray<Diagnostic> Run(string repository)
    {
        var source = Scaffold.Replace("{{REPO}}", repository);

        var compilation = CSharpCompilation.Create(
            "ShiftEntity.GeneratorDiagnosticTests.Sample",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        // A scaffold that doesn't compile resolves no Shift types, so the generator would stay silent and every
        // AssertSilent above would pass for the wrong reason. Fail loudly instead of quietly proving nothing.
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(errors.Count == 0,
            "Test scaffold does not compile:" + Environment.NewLine + string.Join(Environment.NewLine, errors));

        CSharpGeneratorDriver
            .Create(new SourceGenerator.ShiftEntityMapperGenerator().AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);

        return diagnostics;
    }

    /// <summary>
    /// Everything this test host was built against — the framework, EF Core and the ShiftEntity assemblies the
    /// scaffold names. Deduped by simple name because the TPA list can carry a package and framework copy of one
    /// assembly, which Roslyn rejects as an ambiguous reference.
    /// </summary>
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .Select(g => (MetadataReference)MetadataReference.CreateFromFile(g.First()))
            .ToArray();
}
