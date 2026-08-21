using ShiftSoftware.ShiftEntity.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pins the write-direction counterparts of the read-direction narrowing conversions.
/// <para>
/// The read direction has always turned <c>long</c> into <c>string</c> and <c>enum</c> into <c>int</c>. The
/// write direction had no way back, and its convention returning null meant NO ASSIGNMENT WAS EMITTED — so the
/// member displayed correctly, saved nothing, and produced no build output of any kind. A live example was a
/// DTO exposing a <c>string</c> id for a <c>long</c> column on a deep child, where every row was affected.
/// </para>
/// </summary>
public class InverseScalarConventionTests
{
    private const string Scaffold = """
        using System;
        using System.Collections.Generic;
        using ShiftSoftware.ShiftEntity.Core;
        using ShiftSoftware.ShiftEntity.EFCore;
        using ShiftSoftware.ShiftEntity.Model.Dtos;

        namespace Sample;

        public class SampleDb : ShiftDbContext { }

        public enum Grade { None = 0, Silver = 1, Gold = 2 }

        public class Ticket : ShiftEntity<Ticket>
        {
            public long GroupID { get; set; }
            public long? OptionalGroupID { get; set; }
            public Guid Reference { get; set; }
            public Guid? OptionalReference { get; set; }
            public Grade Grade { get; set; }
            public Grade? OptionalGrade { get; set; }
        }

        public class TicketDTO : ShiftEntityViewAndUpsertDTO
        {
            public override string? ID { get; set; }
            public string GroupID { get; set; } = "";
            public string? OptionalGroupID { get; set; }
            public string Reference { get; set; } = "";
            public string? OptionalReference { get; set; }
            public int Grade { get; set; }
            public int? OptionalGrade { get; set; }
        }

        public class TicketRepository : ShiftRepository<SampleDb, Ticket, TicketDTO, TicketDTO>
        {
            public TicketRepository(SampleDb db) : base(db) { }
        }
        """;

    private static readonly Guid Reference = new("11111111-2222-3333-4444-555555555555");

    /// <summary>Out and back, with the values intact — the whole point of the pairing.</summary>
    [Fact]
    public void NarrowedScalars_SurviveARoundTrip()
    {
        var sample = MapperGeneratorHarness.Load(Scaffold);
        var mapper = sample.Mapper("Generated_Ticket_");

        var entity = sample.New("Sample.Ticket",
            ("GroupID", 42L),
            ("OptionalGroupID", 7L),
            ("Reference", Reference),
            ("OptionalReference", Reference),
            ("Grade", Enum.ToObject(sample.Type("Sample.Grade"), 2)),
            ("OptionalGrade", Enum.ToObject(sample.Type("Sample.Grade"), 1)));

        var dto = mapper.MapToView(entity);

        Assert.Equal("42", GeneratedAssembly.Get<string>(dto, "GroupID"));
        Assert.Equal(2, GeneratedAssembly.Get<int>(dto, "Grade"));

        var saved = mapper.MapToEntity(dto, sample.New("Sample.Ticket"));

        // Every one of these wrote nothing at all before this change.
        Assert.Equal(42L, GeneratedAssembly.Get<long>(saved, "GroupID"));
        Assert.Equal(7L, GeneratedAssembly.Get<long?>(saved, "OptionalGroupID"));
        Assert.Equal(Reference, GeneratedAssembly.Get<Guid>(saved, "Reference"));
        Assert.Equal(Reference, GeneratedAssembly.Get<Guid?>(saved, "OptionalReference"));
        Assert.Equal(2, (int)GeneratedAssembly.Get<object>(saved, "Grade")!);
        Assert.Equal(1, (int)GeneratedAssembly.Get<object>(saved, "OptionalGrade")!);
    }

    /// <summary>A blank optional value is an absent value, not a parse failure.</summary>
    [Fact]
    public void BlankOptionalScalars_BecomeNull()
    {
        var sample = MapperGeneratorHarness.Load(Scaffold);
        var mapper = sample.Mapper("Generated_Ticket_");

        var dto = sample.New("Sample.TicketDTO",
            ("GroupID", "1"),
            ("OptionalGroupID", "   "),
            ("Reference", Reference.ToString()),
            ("OptionalReference", null));

        var saved = mapper.MapToEntity(dto, sample.New("Sample.Ticket"));

        Assert.Null(GeneratedAssembly.Get<long?>(saved, "OptionalGroupID"));
        Assert.Null(GeneratedAssembly.Get<Guid?>(saved, "OptionalReference"));
    }

    /// <summary>
    /// Bad input throws, and the message names the member. Writing a default instead would be worse than the
    /// bug this step fixes: a silent <c>0</c> in a required foreign key saves a row pointing at the wrong
    /// parent, and nothing anywhere says so.
    /// </summary>
    [Fact]
    public void MalformedRequiredValue_ThrowsAndNamesTheMember()
    {
        var sample = MapperGeneratorHarness.Load(Scaffold);
        var mapper = sample.Mapper("Generated_Ticket_");

        var dto = sample.New("Sample.TicketDTO",
            ("GroupID", "not-a-number"),
            ("Reference", Reference.ToString()));

        var error = Assert.Throws<ShiftEntityMappingException>(
            () => mapper.MapToEntity(dto, sample.New("Sample.Ticket")));

        Assert.Contains("GroupID", error.Message);
        Assert.Equal("not-a-number", error.Value);
        Assert.Equal(typeof(long), error.TargetType);
    }

    /// <summary>The same contract for the optional overload: blank is null, but rubbish is still rubbish.</summary>
    [Fact]
    public void MalformedOptionalValue_AlsoThrows()
    {
        var sample = MapperGeneratorHarness.Load(Scaffold);
        var mapper = sample.Mapper("Generated_Ticket_");

        var dto = sample.New("Sample.TicketDTO",
            ("GroupID", "1"),
            ("OptionalGroupID", "not-a-number"),
            ("Reference", Reference.ToString()));

        Assert.Throws<ShiftEntityMappingException>(
            () => mapper.MapToEntity(dto, sample.New("Sample.Ticket")));
    }

    /// <summary>
    /// These members stop being reported as unmapped, which is the other half of the value: the diagnostics
    /// turned up in the next steps would otherwise bury real findings under cases the framework should just
    /// handle.
    /// </summary>
    [Fact]
    public void ConvertedMembers_AreNotReportedAsUnmapped()
    {
        var run = MapperGeneratorHarness.Run(Scaffold);

        Assert.Empty(run.OfId("SHENGEN004"));
    }
}
