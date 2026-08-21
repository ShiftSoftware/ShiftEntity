using Microsoft.CodeAnalysis;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pins <c>SHENGEN004</c> — the "generated mapper does not map these members" warning.
/// <para>
/// This is the diagnostic the AutoMapper-removal effort leans on hardest: it is how a programmer finds out that
/// a DTO member silently isn't being mapped. It had no tests at all, so nothing stopped it from being narrowed
/// or muted by accident.
/// </para>
/// <para>
/// The silent cases are as much the contract as the firing one. A warning that also fires on members the
/// programmer has already dealt with — customized, ignored, or cut by cycle detection — is a warning people
/// turn off, and then it protects nobody.
/// </para>
/// </summary>
public class UnmappedMemberDiagnosticTests
{
    private const string Unmapped = "SHENGEN004";

    /// <summary>
    /// <c>Mystery</c> has no counterpart on the entity, so no convention can reach it and it is not a composable
    /// child. Exactly the case that must be visible: the DTO field would come back null forever, silently.
    /// </summary>
    [Fact]
    public void MemberWithNoEntityCounterpart_Warns()
    {
        var diagnostic = Assert.Single(Run("""
            public class WidgetDTO : ShiftEntityViewAndUpsertDTO
            {
                public override string? ID { get; set; }
                public string Name { get; set; } = "";
                public string Mystery { get; set; } = "";
            }
            """));

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);

        // The member must be nameable from the message — it is the only thing that makes the warning actionable.
        Assert.Contains("Mystery", diagnostic.GetMessage());
    }

    /// <summary>
    /// The warning has to be navigable. For the dominant shape — a repository triple with no
    /// <c>[ShiftEntityMapper]</c> partial to point at — there is no user class, and the report used to fall
    /// straight through to <c>Location.None</c>: a warning with no file and no line, which cannot be
    /// double-clicked and cannot be suppressed locally. That is the difference between warnings a team triages
    /// and warnings a team switches off. It points at the repository declaration instead.
    /// </summary>
    [Fact]
    public void Warning_PointsAtTheRepository_WhenThereIsNoMapperClass()
    {
        var diagnostic = Assert.Single(Run("""
            public class WidgetDTO : ShiftEntityViewAndUpsertDTO
            {
                public override string? ID { get; set; }
                public string Name { get; set; } = "";
                public string Mystery { get; set; } = "";
            }
            """));

        Assert.NotEqual(Location.None, diagnostic.Location);

        var span = diagnostic.Location.GetLineSpan();
        Assert.False(string.IsNullOrEmpty(span.Path));

        // Read the line back out of the very tree the diagnostic points into — that is what "navigable" means.
        var line = diagnostic.Location.SourceTree!.GetText().Lines[span.StartLinePosition.Line].ToString();
        Assert.Contains("WidgetRepository", line);
    }

    /// <summary>A member the programmer mapped by hand is handled, not unmapped.</summary>
    [Fact]
    public void CustomConfiguredMember_IsSilent() => AssertSilent("""
        public class WidgetDTO : ShiftEntityViewAndUpsertDTO
        {
            public override string? ID { get; set; }
            public string Name { get; set; } = "";
            public string Mystery { get; set; } = "";
        }

        [ShiftEntityMapper]
        public partial class WidgetMapper : IShiftEntityMapper<Widget, WidgetDTO, WidgetDTO>
        {
            partial void Configure(ShiftMapperBuilder<Widget, WidgetDTO, WidgetDTO> map)
            {
                map.ForView(d => d.Mystery, (e, ctx) => "resolved");
            }
        }
        """);

    /// <summary>
    /// <c>[ShiftEntityMapperIgnore]</c> is the programmer saying "leave this alone". Warning about it afterwards
    /// would make the attribute useless.
    /// </summary>
    [Fact]
    public void AttributeIgnoredMember_IsSilent() => AssertSilent("""
        public class WidgetDTO : ShiftEntityViewAndUpsertDTO
        {
            public override string? ID { get; set; }
            public string Name { get; set; } = "";

            [ShiftEntityMapperIgnore]
            public string Mystery { get; set; } = "";
        }
        """);

    /// <summary>
    /// A cycle-skipped child already reports <c>SHENGEN003</c>, which names the member and says what to do.
    /// Reporting SHENGEN004 as well would double-report one problem under two ids.
    /// </summary>
    [Fact]
    public void CycleSkippedChild_IsSilentForThisDiagnostic()
    {
        var run = MapperGeneratorHarness.Run($$"""
            {{Preamble}}

            public class Node : ShiftEntity<Node>
            {
                public string Name { get; set; } = "";
                public Branch Branch { get; set; } = new();
            }

            public class Branch
            {
                public string Label { get; set; } = "";
                public Node Back { get; set; } = new();
            }

            public class BranchDTO
            {
                public string Label { get; set; } = "";
                public NodeDTO Back { get; set; } = new();
            }

            public class NodeDTO : ShiftEntityViewAndUpsertDTO
            {
                public override string? ID { get; set; }
                public string Name { get; set; } = "";
                public BranchDTO Branch { get; set; } = new();
            }

            public class NodeRepository : ShiftRepository<SampleDb, Node, NodeDTO, NodeDTO>
            {
                public NodeRepository(SampleDb db) : base(db) { }
            }
            """);

        // The cycle must actually be detected, or this test proves nothing.
        Assert.NotEmpty(run.OfId("SHENGEN003"));

        Assert.DoesNotContain(run.OfId(Unmapped),
            d => d.GetMessage().Contains("Branch", StringComparison.Ordinal) ||
                 d.GetMessage().Contains("Back", StringComparison.Ordinal));
    }

    // ──────────────────────────────── harness ────────────────────────────────

    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using ShiftSoftware.ShiftEntity.Core;
        using ShiftSoftware.ShiftEntity.EFCore;
        using ShiftSoftware.ShiftEntity.Model.Dtos;

        namespace Sample;

        public class SampleDb : ShiftDbContext { }
        """;

    /// <summary>The fixed half: a Widget entity and a repository. The DTO is the half under test.</summary>
    private static string Scaffold(string dto) => $$"""
        {{Preamble}}

        public class Widget : ShiftEntity<Widget>
        {
            public string Name { get; set; } = "";
        }

        {{dto}}

        public class WidgetRepository : ShiftRepository<SampleDb, Widget, WidgetDTO, WidgetDTO>
        {
            public WidgetRepository(SampleDb db) : base(db) { }
        }
        """;

    private static void AssertSilent(string dto) => Assert.Empty(Run(dto));

    private static IEnumerable<Diagnostic> Run(string dto) =>
        MapperGeneratorHarness.Run(Scaffold(dto)).OfId(Unmapped);
}
