using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pins the READ/WRITE SYMMETRY of automatic deep mapping: whatever the generator composes into a DTO on the way
/// out it must also compose back into the entity on the way in. The two directions used to disagree — the view
/// side composed any pairable child, while the entity side only composed members that were ShiftEntity
/// NAVIGATIONS. A plain child POCO (a JSON-owned column type, an EF owned type) is not a navigation, so it was
/// read back fine and silently dropped on save: the child list simply came back empty after every upsert.
/// <para>
/// The scaffold is the shape that surfaced it — a triple whose child is a plain POCO that itself holds a
/// grandchild list, so both the triple mapper and the generated PAIR mapper are covered.
/// </para>
/// </summary>
public class GeneratedDeepWriteTests
{
    private const string Scaffold = """
        using System.Collections.Generic;
        using ShiftSoftware.ShiftEntity.Core;
        using ShiftSoftware.ShiftEntity.EFCore;
        using ShiftSoftware.ShiftEntity.Model.Dtos;

        namespace Sample;

        public class SampleDb : ShiftDbContext { }

        // ── plain POCOs: no ShiftEntity base anywhere in the child tree ──
        public class ShiftItem
        {
            public string Title { get; set; } = "";
            public long StartTicks { get; set; }
        }

        public class ShiftGroup
        {
            public List<int>? Days { get; set; }
            public List<ShiftItem> Items { get; set; } = new();
        }

        public class ShiftItemDTO
        {
            public string Title { get; set; } = "";
            public long StartTicks { get; set; }
        }

        public class ShiftGroupDTO
        {
            public List<int>? Days { get; set; }
            public List<ShiftItemDTO> Items { get; set; } = new();
        }

        public class ScheduleDTO : ShiftEntityDTOBase
        {
            public override string? ID { get; set; }
            public string Name { get; set; } = "";
            public List<ShiftGroupDTO> Groups { get; set; } = new();
        }

        public class Schedule : ShiftEntity<Schedule>
        {
            public string Name { get; set; } = "";
            public List<ShiftGroup> Groups { get; set; } = new();
        }

        public class ScheduleRepository : ShiftRepository<SampleDb, Schedule, ScheduleDTO, ScheduleDTO>
        {
            public ScheduleRepository(SampleDb db) : base(db) { }
        }
        """;

    /// <summary>The triple mapper composes the plain child collection in BOTH directions.</summary>
    [Fact]
    public void PlainChildCollection_IsComposedOnTheEntitySide_NotOnlyTheViewSide()
    {
        var triple = Generate().Single(s => s.Name.Contains("Generated_Schedule_"));

        Assert.Contains("dto.Groups = entity.Groups == null ? null", triple.Text);
        Assert.Contains(".Map(__c, context)", triple.Text);

        // The regression: this assignment was missing entirely, so every upsert wrote an empty list.
        Assert.Contains("existing.Groups = dto.Groups == null ? null", triple.Text);
        Assert.Contains(".MapBack(__d, new global::Sample.ShiftGroup(), context)", triple.Text);
    }

    /// <summary>
    /// Same symmetry one level down. This is where it actually bit: the child pair's MapBack dropped the
    /// grandchild list, so groups saved but arrived with no items inside them.
    /// </summary>
    [Fact]
    public void PlainGrandchildCollection_IsComposedByTheGeneratedPairMapper()
    {
        var pair = Generate().Single(s => s.Name.Contains("Generated_Pair_ShiftGroup_ShiftGroupDTO"));

        Assert.Contains("dto.Items = source.Items == null ? null", pair.Text);
        Assert.Contains("existing.Items = dto.Items == null ? null", pair.Text);
        Assert.Contains(".MapBack(__d, new global::Sample.ShiftItem(), context)", pair.Text);
    }

    /// <summary>Audit/base members are the pipeline's to write and must never be composed into the entity.</summary>
    [Fact]
    public void AuditMembers_AreNeverWrittenBackToTheEntity()
    {
        var triple = Generate().Single(s => s.Name.Contains("Generated_Schedule_"));
        var entityBody = triple.Text.Split("MapToEntityGenerated")[1];

        foreach (var excluded in new[] { "existing.ID", "existing.CreateDate", "existing.LastSaveDate", "existing.IsDeleted" })
            Assert.DoesNotContain(excluded, entityBody);
    }

    // ──────────────────────────────── harness ────────────────────────────────

    /// <summary>
    /// Runs the generator over the scaffold and returns its output. The emitted code is compiled along with the
    /// scaffold, so a mapper that does not build fails here rather than in a consumer's repository.
    /// </summary>
    private static List<(string Name, string Text)> Generate()
    {
        var compilation = CSharpCompilation.Create(
            "ShiftEntity.GeneratedDeepWriteTests.Sample",
            [CSharpSyntaxTree.ParseText(Scaffold, new CSharpParseOptions(LanguageVersion.Latest))],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var scaffoldErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(scaffoldErrors.Count == 0,
            "Test scaffold does not compile:" + Environment.NewLine + string.Join(Environment.NewLine, scaffoldErrors));

        var driver = CSharpGeneratorDriver
            .Create(new SourceGenerator.ShiftEntityMapperGenerator().AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var emitErrors = output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(emitErrors.Count == 0,
            "Generated mappers do not compile:" + Environment.NewLine + string.Join(Environment.NewLine, emitErrors));

        return driver.GetRunResult().Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => (s.HintName, s.SourceText.ToString()))
            .ToList();
    }

    /// <inheritdoc cref="GeneratorDiagnosticTests"/>
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .Select(g => (MetadataReference)MetadataReference.CreateFromFile(g.First()))
            .ToArray();
}
