using Microsoft.CodeAnalysis;
using ShiftSoftware.ShiftEntity.Core;
using System.Linq.Expressions;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pins the safety net around automatic deep WRITE.
/// <para>
/// Deep write is replace-with-new: every child DTO becomes a brand new child entity. For a plain owned POCO
/// that is exactly right. For children that are tracked rows with their own identity and a required foreign key
/// back to the parent it is not — the framework forces Restrict on every non-ownership foreign key, so saving
/// either throws or orphans and duplicates rows, silently.
/// </para>
/// <para>
/// The fix is deliberately NOT to turn the default off: doing that re-opens the bug it was introduced to fix,
/// where JSON-owned grandchildren read back fine and were silently emptied on save. Instead the dangerous case
/// is made loud at build time, and given somewhere to go.
/// </para>
/// </summary>
public class DeepWriteSafetyTests
{
    private const string ReplacesTracked = "SHENGEN010";

    /// <summary>A tracked child with a required back-reference cannot be replaced. Say so at build time.</summary>
    [Fact]
    public void TrackedChildWithRequiredBackReference_Warns()
    {
        var diagnostic = Assert.Single(MapperGeneratorHarness.Run(TrackedChildScaffold).OfId(ReplacesTracked));

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);

        var message = diagnostic.GetMessage();
        Assert.Contains("Lines", message);
        Assert.Contains("InvoiceLine", message);
        Assert.Contains("InvoiceID", message);

        // It must name a way out, not just a problem.
        Assert.Contains("AfterEntity", message);
    }

    /// <summary>
    /// A plain owned POCO has no identity and no foreign key, so replace-with-new is correct and silent. This is
    /// the shape the deep-write feature exists for, and warning about it would bury the real finding.
    /// </summary>
    [Fact]
    public void PlainOwnedChild_IsSilent()
    {
        var run = MapperGeneratorHarness.Run("""
            using System;
            using System.Collections.Generic;
            using ShiftSoftware.ShiftEntity.Core;
            using ShiftSoftware.ShiftEntity.EFCore;
            using ShiftSoftware.ShiftEntity.Model.Dtos;

            namespace Sample;

            public class SampleDb : ShiftDbContext { }

            public class Line { public string Text { get; set; } = ""; }
            public class LineDTO { public string Text { get; set; } = ""; }

            public class Note : ShiftEntity<Note>
            {
                public List<Line> Lines { get; set; } = new();
            }

            public class NoteDTO : ShiftEntityViewAndUpsertDTO
            {
                public override string? ID { get; set; }
                public List<LineDTO> Lines { get; set; } = new();
            }

            public class NoteRepository : ShiftRepository<SampleDb, Note, NoteDTO, NoteDTO>
            {
                public NoteRepository(SampleDb db) : base(db) { }
            }
            """);

        Assert.Empty(run.OfId(ReplacesTracked));
    }

    /// <summary>
    /// The escape hatch has to actually work. An <c>AfterEntity</c> hook runs after the generated body with both
    /// the DTO and the entity being updated, which is what reconciling a collection by business key needs —
    /// and, before this, the only way to get it was to take the whole method over by hand.
    /// </summary>
    [Fact]
    public void AfterEntityHook_RunsWithTheEntityBeingUpdated()
    {
        var sample = MapperGeneratorHarness.Load(TrackedChildScaffold);
        var mapper = sample.Mapper("Generated_Invoice_");

        var seen = new List<string>();
        mapper.AddConfiguration(AfterEntityHook(mapper, sample, seen));

        var dto = sample.New("Sample.InvoiceDTO", ("Number", "INV-9"));
        var existing = sample.New("Sample.Invoice", ("Number", "INV-1"));

        mapper.MapToEntity(dto, existing);

        // The hook saw the entity AFTER the generated body wrote to it — that ordering is the contract.
        Assert.Equal(["INV-9"], seen);
    }

    /// <summary>
    /// The hook is additive: registering one must not disturb what the generated body already does.
    /// </summary>
    [Fact]
    public void AfterEntityHook_DoesNotReplaceTheGeneratedBody()
    {
        var sample = MapperGeneratorHarness.Load(TrackedChildScaffold);
        var mapper = sample.Mapper("Generated_Invoice_");

        mapper.AddConfiguration(AfterEntityHook(mapper, sample, []));

        var saved = mapper.MapToEntity(
            sample.New("Sample.InvoiceDTO", ("Number", "INV-9")),
            sample.New("Sample.Invoice"));

        Assert.Equal("INV-9", GeneratedAssembly.Get<string>(saved, "Number"));
    }

    // ──────────────────────────────── harness ────────────────────────────────

    private const string TrackedChildScaffold = """
        using System;
        using System.Collections.Generic;
        using ShiftSoftware.ShiftEntity.Core;
        using ShiftSoftware.ShiftEntity.EFCore;
        using ShiftSoftware.ShiftEntity.Model.Dtos;

        namespace Sample;

        public class SampleDb : ShiftDbContext { }

        // A tracked row with a REQUIRED foreign key back to its parent — the shape that cannot be replaced.
        public class InvoiceLine : ShiftEntity<InvoiceLine>
        {
            public long InvoiceID { get; set; }
            public string Description { get; set; } = "";
        }

        public class InvoiceLineDTO
        {
            public string Description { get; set; } = "";
        }

        public class Invoice : ShiftEntity<Invoice>
        {
            public string Number { get; set; } = "";
            public List<InvoiceLine> Lines { get; set; } = new();
        }

        public class InvoiceDTO : ShiftEntityViewAndUpsertDTO
        {
            public override string? ID { get; set; }
            public string Number { get; set; } = "";
            public List<InvoiceLineDTO> Lines { get; set; } = new();
        }

        public class InvoiceRepository : ShiftRepository<SampleDb, Invoice, InvoiceListDTO, InvoiceDTO>
        {
            public InvoiceRepository(SampleDb db) : base(db) { }
        }

        public class InvoiceListDTO : ShiftEntityListDTO
        {
            public override string? ID { get; set; }
            public string Number { get; set; } = "";
        }
        """;

    /// <summary>
    /// Builds <c>map =&gt; map.AfterEntity((dto, entity, ctx) =&gt; seen.Add(entity.Number))</c>. Assembled with
    /// expression trees because the entity and DTO types only exist in the generated assembly.
    /// </summary>
    private static object AfterEntityHook(Mapper mapper, GeneratedAssembly sample, List<string> seen)
    {
        var builderType = mapper.BuilderType;
        var arguments = builderType.GetGenericArguments();

        var dto = Expression.Parameter(arguments[2], "dto");
        var entity = Expression.Parameter(arguments[0], "entity");
        var context = Expression.Parameter(typeof(MappingContext), "context");

        var hook = Expression.Lambda(
            typeof(Action<,,>).MakeGenericType(arguments[2], arguments[0], typeof(MappingContext)),
            Expression.Call(Expression.Constant(seen), typeof(List<string>).GetMethod("Add")!,
                Expression.Property(entity, "Number")),
            dto, entity, context);

        var builder = Expression.Parameter(builderType, "map");

        return Expression.Lambda(
            typeof(Action<>).MakeGenericType(builderType),
            Expression.Call(builder, builderType.GetMethod("AfterEntity")!, hook),
            builder).Compile();
    }
}
