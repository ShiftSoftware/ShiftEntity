using Microsoft.CodeAnalysis;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pins <c>SHENGEN007</c> — the list-direction counterpart of <c>SHENGEN004</c>.
/// <para>
/// The list projection had no unmapped channel at all. A list-DTO member the generator could not map was simply
/// left out of the projection: the grid column came back empty, the build said nothing, and the only way to
/// find it was to notice the blank column. This is the one warning expected to arrive in bulk the first time,
/// and that is the point — it is how the remaining work gets sized honestly instead of by grepping.
/// </para>
/// </summary>
public class UnmappedListMemberDiagnosticTests
{
    private const string UnmappedList = "SHENGEN007";

    /// <summary>
    /// <c>CampaignName</c> has no counterpart on the entity, so nothing projects into it.
    /// <para>
    /// The message must carry a paste-ready <c>ForList</c> line. Flattening is deliberately NOT implemented — it
    /// would reach two levels into the entity invisibly, which is the coupling this whole effort exists to
    /// remove — so printing the exact line to paste is what makes that decision affordable.
    /// </para>
    /// </summary>
    [Fact]
    public void MemberWithNoEntityCounterpart_WarnsWithAPasteReadyFix()
    {
        var diagnostic = Assert.Single(Run("""
            public class DealListDTO : ShiftEntityListDTO
            {
                public override string? ID { get; set; }
                public string Title { get; set; } = "";
                public string CampaignName { get; set; } = "";
            }
            """));

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);

        var message = diagnostic.GetMessage();
        Assert.Contains("CampaignName", message);

        // The suggestion is filled in because the name reads as a flattened path and both halves exist.
        Assert.Contains("map.ForList(d => d.CampaignName, e => e.Campaign.Name)", message);
    }

    /// <summary>Where the name says nothing useful, the line still comes out — with the value left blank.</summary>
    [Fact]
    public void MemberThatLooksLikeNothing_StillGetsAFixLine()
    {
        var diagnostic = Assert.Single(Run("""
            public class DealListDTO : ShiftEntityListDTO
            {
                public override string? ID { get; set; }
                public string Title { get; set; } = "";
                public string Mystery { get; set; } = "";
            }
            """));

        Assert.Contains("map.ForList(d => d.Mystery, e => e.…)", diagnostic.GetMessage());
    }

    /// <summary>A <c>ForList</c> customization is composed at runtime — the member is handled, not missing.</summary>
    [Fact]
    public void ForListConfiguredMember_IsSilent() => AssertSilent("""
        public class DealListDTO : ShiftEntityListDTO
        {
            public override string? ID { get; set; }
            public string Title { get; set; } = "";
            public string CampaignName { get; set; } = "";
        }

        [ShiftEntityMapper]
        public partial class DealMapper : IShiftEntityMapper<Deal, DealListDTO, DealDTO>
        {
            partial void Configure(ShiftMapperBuilder<Deal, DealListDTO, DealDTO> map)
            {
                map.ForList(d => d.CampaignName, e => e.Campaign.Name);
            }
        }
        """);

    /// <summary>
    /// Suppressing the WARNING for a ForList member must not suppress its BINDING.
    /// <para>
    /// ComposeList layers the customization on at runtime, but only where the configuration is actually applied.
    /// Registered from a REPOSITORY (the shape below, and the one the sample project uses), the generated mapper
    /// itself carries no Configure hook — so a mapper resolved straight from the registry has nothing but what
    /// was baked. Dropping the convention binding along with the warning made that case return null, which is
    /// exactly the silent-empty-column failure this diagnostic exists to prevent.
    /// </para>
    /// </summary>
    [Fact]
    public void ForListConfiguredMember_StillGetsItsConventionBinding()
    {
        const string repositoryConfigured = """
            public class DealListDTO : ShiftEntityListDTO
            {
                public override string? ID { get; set; }
                public string Title { get; set; } = "";
            }

            public class DealConfiguringRepository : ShiftRepository<SampleDb, Deal, DealListDTO, DealDTO>
            {
                public DealConfiguringRepository(SampleDb db)
                    : base(db, o => o.UseGeneratedMapper(map => map.ForList(d => d.Title, e => e.Title + " (customized)"))) { }
            }
            """;

        // The binding has to be in the emitted projection, not merely produced once configuration is applied.
        var projection = MapperGeneratorHarness.Run(Scaffold(repositoryConfigured))
            .Source("Generated_Deal_")
            .Split("__shiftListProjection")[1];

        Assert.Contains("Title = e.Title", projection);

        // ...and the member is still not reported, because the programmer has taken it over.
        Assert.Empty(MapperGeneratorHarness.Run(Scaffold(repositoryConfigured)).OfId(UnmappedList));
    }

    /// <summary>An explicitly ignored list column is a decision already taken.</summary>
    [Fact]
    public void IgnoreListedMember_IsSilent() => AssertSilent("""
        public class DealListDTO : ShiftEntityListDTO
        {
            public override string? ID { get; set; }
            public string Title { get; set; } = "";
            public string CampaignName { get; set; } = "";
        }

        [ShiftEntityMapper]
        public partial class DealMapper : IShiftEntityMapper<Deal, DealListDTO, DealDTO>
        {
            partial void Configure(ShiftMapperBuilder<Deal, DealListDTO, DealDTO> map)
            {
                map.IgnoreList(d => d.CampaignName);
            }
        }
        """);

    /// <summary>Same, spelled on the property itself.</summary>
    [Fact]
    public void AttributeIgnoredMember_IsSilent() => AssertSilent("""
        public class DealListDTO : ShiftEntityListDTO
        {
            public override string? ID { get; set; }
            public string Title { get; set; } = "";

            [ShiftEntityMapperIgnore]
            public string CampaignName { get; set; } = "";
        }
        """);

    /// <summary>
    /// A list DTO whose members all project cleanly must stay quiet. Worth pinning on its own: a warning that
    /// fires on healthy code is one people switch off, and then it protects nothing.
    /// </summary>
    [Fact]
    public void FullyMappableListDto_IsSilent() => AssertSilent("""
        public class DealListDTO : ShiftEntityListDTO
        {
            public override string? ID { get; set; }
            public string Title { get; set; } = "";
        }
        """);

    // ──────────────────────────────── harness ────────────────────────────────

    /// <summary>Deal has a Campaign navigation, so "CampaignName" is a name the suggestion can resolve.</summary>
    private static string Scaffold(string listDto) => $$"""
        using System;
        using System.Collections.Generic;
        using ShiftSoftware.ShiftEntity.Core;
        using ShiftSoftware.ShiftEntity.EFCore;
        using ShiftSoftware.ShiftEntity.Model.Dtos;

        namespace Sample;

        public class SampleDb : ShiftDbContext { }

        public class Campaign : ShiftEntity<Campaign>
        {
            public string Name { get; set; } = "";
        }

        public class Deal : ShiftEntity<Deal>
        {
            public string Title { get; set; } = "";
            public long CampaignID { get; set; }
            public Campaign Campaign { get; set; } = new();
        }

        public class DealDTO : ShiftEntityViewAndUpsertDTO
        {
            public override string? ID { get; set; }
            public string Title { get; set; } = "";
        }

        {{listDto}}

        public class DealRepository : ShiftRepository<SampleDb, Deal, DealListDTO, DealDTO>
        {
            public DealRepository(SampleDb db) : base(db) { }
        }
        """;

    private static void AssertSilent(string listDto) => Assert.Empty(Run(listDto));

    private static IEnumerable<Diagnostic> Run(string listDto) =>
        MapperGeneratorHarness.Run(Scaffold(listDto)).OfId(UnmappedList);
}
