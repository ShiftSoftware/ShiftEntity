using Microsoft.CodeAnalysis;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pins how member names are matched.
/// <para>
/// Every lookup used to be a <c>ToDictionary(p =&gt; p.Name)</c> with the default ordinal comparer, so
/// <c>CompanyID</c> and <c>CompanyId</c> did not match, the convention returned null, and — as everywhere in
/// this generator — no assignment was emitted at all. That is not a rule the framework chose; it is behaviour
/// AutoMapper had and the generated mapper lost, which is why insensitive is the DEFAULT and exact-case is the
/// deliberate opt-in.
/// </para>
/// </summary>
public class CaseInsensitiveMatchingTests
{
    private const string Ambiguous = "SHENGEN011";

    /// <summary>
    /// The regression this exists for: entity <c>CompanyID</c> (<c>long?</c>) against DTO <c>CompanyId</c>
    /// (<c>string?</c>), with no configuration at all. It has to work in all three directions.
    /// </summary>
    [Fact]
    public void CaseDifferentMember_MapsInEveryDirection()
    {
        var sample = MapperGeneratorHarness.Load(Scaffold());
        var mapper = sample.Mapper("Generated_Branch_");

        var entity = sample.New("Sample.Branch", ("Title", "HQ"), ("CompanyID", 42L));

        var dto = mapper.MapToView(entity);
        Assert.Equal("42", GeneratedAssembly.Get<string>(dto, "CompanyId"));

        var saved = mapper.MapToEntity(dto, sample.New("Sample.Branch"));
        Assert.Equal(42L, GeneratedAssembly.Get<long?>(saved, "CompanyID"));

        var row = Assert.Single(mapper.MapToList(sample.Queryable("Sample.Branch", entity)));
        Assert.Equal("42", GeneratedAssembly.Get<string>(row, "CompanyId"));
    }

    /// <summary>A successful case-insensitive match is silent — parity, not a decision worth reporting.</summary>
    [Fact]
    public void CaseDifferentMember_IsNotReported()
    {
        var run = MapperGeneratorHarness.Run(Scaffold());

        Assert.Empty(run.OfId("SHENGEN004"));
        Assert.Empty(run.OfId("SHENGEN007"));
        Assert.Empty(run.OfId(Ambiguous));
    }

    /// <summary>
    /// Exact case always wins and is never beaten by a looser candidate. This is what makes the fallback safe:
    /// a type carrying both <c>Code</c> and <c>CODE</c> binds each to its own exactly-named member, so only a
    /// member with no exact counterpart can ever reach the ambiguous branch.
    /// </summary>
    [Fact]
    public void ExactMatchWins_WhenBothSpellingsExist()
    {
        var sample = MapperGeneratorHarness.Load("""
            using System;
            using ShiftSoftware.ShiftEntity.Core;
            using ShiftSoftware.ShiftEntity.EFCore;
            using ShiftSoftware.ShiftEntity.Model.Dtos;

            namespace Sample;

            public class SampleDb : ShiftDbContext { }

            public class Pair : ShiftEntity<Pair>
            {
                public string Code { get; set; } = "";
                public string CODE { get; set; } = "";
            }

            public class PairDTO : ShiftEntityViewAndUpsertDTO
            {
                public override string? ID { get; set; }
                public string Code { get; set; } = "";
                public string CODE { get; set; } = "";
            }

            public class PairRepository : ShiftRepository<SampleDb, Pair, PairDTO, PairDTO>
            {
                public PairRepository(SampleDb db) : base(db) { }
            }
            """);

        var mapper = sample.Mapper("Generated_Pair_");
        var entity = sample.New("Sample.Pair", ("Code", "lower"), ("CODE", "UPPER"));

        var dto = mapper.MapToView(entity);

        // No swap, and no ambiguity: each name found its own exact home.
        Assert.Equal("lower", GeneratedAssembly.Get<string>(dto, "Code"));
        Assert.Equal("UPPER", GeneratedAssembly.Get<string>(dto, "CODE"));
    }

    /// <summary>
    /// Two spellings and no exact match: the generator refuses to guess. It skips the member and warns, naming
    /// both candidates — and the build still SUCCEEDS. A warning rather than an error because the member is
    /// merely skipped, which is the framework's own split between the two.
    /// </summary>
    [Fact]
    public void AmbiguousMatch_SkipsTheMemberAndWarns()
    {
        var run = MapperGeneratorHarness.Run("""
            using System;
            using ShiftSoftware.ShiftEntity.Core;
            using ShiftSoftware.ShiftEntity.EFCore;
            using ShiftSoftware.ShiftEntity.Model.Dtos;

            namespace Sample;

            public class SampleDb : ShiftDbContext { }

            public class Muddle : ShiftEntity<Muddle>
            {
                public string Code { get; set; } = "";
                public string code { get; set; } = "";
            }

            public class MuddleDTO : ShiftEntityViewAndUpsertDTO
            {
                public override string? ID { get; set; }
                public string CODE { get; set; } = "";
            }

            public class MuddleRepository : ShiftRepository<SampleDb, Muddle, MuddleDTO, MuddleDTO>
            {
                public MuddleRepository(SampleDb db) : base(db) { }
            }
            """);

        var diagnostic = Assert.Single(run.OfId(Ambiguous));

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);

        var message = diagnostic.GetMessage();
        Assert.Contains("CODE", message);
        Assert.Contains("Code", message);
        Assert.Contains("code", message);
    }

    /// <summary>
    /// The opt-out, and the only spelling of it: <c>map.CaseSensitive()</c>. Under it a near-miss is simply
    /// unmapped, and reported like any other unmapped member rather than quietly bound.
    /// </summary>
    [Fact]
    public void CaseSensitiveOptOut_LeavesTheMemberUnmapped()
    {
        var run = MapperGeneratorHarness.Run(Scaffold(builder: "o => o.UseGeneratedMapper(map => map.CaseSensitive())"));

        Assert.Contains(run.OfId("SHENGEN004"), d => d.GetMessage().Contains("CompanyId", StringComparison.Ordinal));
        Assert.Contains(run.OfId("SHENGEN007"), d => d.GetMessage().Contains("CompanyId", StringComparison.Ordinal));

        // A near-miss is unmapped, not ambiguous — there is nothing to disambiguate.
        Assert.Empty(run.OfId(Ambiguous));
    }

    /// <summary>
    /// The setting reaches CHILD mappers too. A root that asked for exact case should not quietly relax the
    /// rule at its first child, which would be the same silent inconsistency this step exists to remove.
    /// </summary>
    [Fact]
    public void CaseSensitiveOptOut_ReachesChildMappers()
    {
        var run = MapperGeneratorHarness.Run("""
            using System;
            using System.Collections.Generic;
            using ShiftSoftware.ShiftEntity.Core;
            using ShiftSoftware.ShiftEntity.EFCore;
            using ShiftSoftware.ShiftEntity.Model.Dtos;

            namespace Sample;

            public class SampleDb : ShiftDbContext { }

            public class Line
            {
                public long GroupID { get; set; }
            }

            public class LineDTO
            {
                public string? GroupId { get; set; }
            }

            public class Order : ShiftEntity<Order>
            {
                public List<Line> Lines { get; set; } = new();
            }

            public class OrderDTO : ShiftEntityViewAndUpsertDTO
            {
                public override string? ID { get; set; }
                public List<LineDTO> Lines { get; set; } = new();
            }

            public class OrderRepository : ShiftRepository<SampleDb, Order, OrderDTO, OrderDTO>
            {
                public OrderRepository(SampleDb db)
                    : base(db, o => o.UseGeneratedMapper(map => map.CaseSensitive())) { }
            }
            """);

        // The child pair maps GroupID -> GroupId only when case is ignored, so under the opt-out it is unmapped.
        Assert.Contains(run.OfId("SHENGEN004"), d => d.GetMessage().Contains("GroupId", StringComparison.Ordinal));
    }

    // ──────────────────────────────── harness ────────────────────────────────

    private static string Scaffold(string? builder = null) => $$"""
        using System;
        using ShiftSoftware.ShiftEntity.Core;
        using ShiftSoftware.ShiftEntity.EFCore;
        using ShiftSoftware.ShiftEntity.Model.Dtos;

        namespace Sample;

        public class SampleDb : ShiftDbContext { }

        public class Branch : ShiftEntity<Branch>
        {
            public string Title { get; set; } = "";
            public long? CompanyID { get; set; }
        }

        public class BranchDTO : ShiftEntityViewAndUpsertDTO
        {
            public override string? ID { get; set; }
            public string Title { get; set; } = "";
            public string? CompanyId { get; set; }
        }

        public class BranchListDTO : ShiftEntityListDTO
        {
            public override string? ID { get; set; }
            public string Title { get; set; } = "";
            public string? CompanyId { get; set; }
        }

        public class BranchRepository : ShiftRepository<SampleDb, Branch, BranchListDTO, BranchDTO>
        {
            public BranchRepository(SampleDb db) : base(db{{(builder is null ? "" : ", " + builder)}}) { }
        }
        """;
}
