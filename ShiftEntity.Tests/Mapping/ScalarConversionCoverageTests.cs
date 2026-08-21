using ShiftSoftware.ShiftEntity.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pins the WIDE scalar conversion set — every simple type a DTO commonly stores differently from its entity.
/// <para>
/// The generator used to hand-list a handful of pairs per direction, which left the line in a strange place:
/// <c>long</c> ↔ <c>string</c> was supported, <c>int</c> ↔ <c>string</c> was not, and nothing between two
/// numeric types was. A missing conversion emits no assignment at all, so those members read back empty and
/// silently never saved.
/// </para>
/// <para>
/// Text is parsed and formatted with the INVARIANT culture, always: a DTO value crosses machines and locales,
/// and a decimal written on one server has to read back identically on another.
/// </para>
/// </summary>
public class ScalarConversionCoverageTests
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

        public class Row : ShiftEntity<Row>
        {
            public int IntToText { get; set; }
            public decimal DecimalToText { get; set; }
            public double DoubleToText { get; set; }
            public bool BoolToText { get; set; }
            public DateTime DateToText { get; set; }
            public Grade EnumToText { get; set; }
            public int? OptionalIntToText { get; set; }

            public string TextToInt { get; set; } = "";
            public string TextToDecimal { get; set; } = "";
            public string TextToBool { get; set; } = "";
            public string TextToDate { get; set; } = "";
            public string TextToEnum { get; set; } = "";
            public string TextToOptionalInt { get; set; } = "";

            public long NarrowToInt { get; set; }
            public int WidenToDecimal { get; set; }
        }

        public class RowDTO : ShiftEntityViewAndUpsertDTO
        {
            public override string? ID { get; set; }
            public string IntToText { get; set; } = "";
            public string DecimalToText { get; set; } = "";
            public string DoubleToText { get; set; } = "";
            public string BoolToText { get; set; } = "";
            public string DateToText { get; set; } = "";
            public string EnumToText { get; set; } = "";
            public string? OptionalIntToText { get; set; }

            public int TextToInt { get; set; }
            public decimal TextToDecimal { get; set; }
            public bool TextToBool { get; set; }
            public DateTime TextToDate { get; set; }
            public Grade TextToEnum { get; set; }
            public int? TextToOptionalInt { get; set; }

            public int NarrowToInt { get; set; }
            public decimal WidenToDecimal { get; set; }
        }

        public class RowRepository : ShiftRepository<SampleDb, Row, RowDTO, RowDTO>
        {
            public RowRepository(SampleDb db) : base(db) { }
        }
        """;

    /// <summary>Every member above is now mapped — none of them was before.</summary>
    [Fact]
    public void WideScalarSet_IsFullyMapped() =>
        Assert.Empty(MapperGeneratorHarness.Run(Scaffold).OfId("SHENGEN004"));

    /// <summary>And every one of them can be written back, so nothing displays-but-never-saves.</summary>
    [Fact]
    public void WideScalarSet_IsSymmetric() =>
        Assert.Empty(MapperGeneratorHarness.Run(Scaffold).OfId("SHENGEN008"));

    [Fact]
    public void ValuesToText_UseTheInvariantCulture()
    {
        var sample = MapperGeneratorHarness.Load(Scaffold);
        var mapper = sample.Mapper("Generated_Row_");

        var entity = sample.New("Sample.Row",
            ("IntToText", 42),
            ("DecimalToText", 1234.56m),
            ("BoolToText", true),
            ("DateToText", new DateTime(2026, 8, 21, 13, 45, 0)),
            ("EnumToText", Enum.ToObject(sample.Type("Sample.Grade"), 2)),
            ("OptionalIntToText", 7),
            ("TextToInt", "1"), ("TextToDecimal", "1"), ("TextToBool", "true"),
            ("TextToDate", "2026-01-01"), ("TextToEnum", "Gold"), ("TextToOptionalInt", "1"));

        var dto = mapper.MapToView(entity);

        Assert.Equal("42", GeneratedAssembly.Get<string>(dto, "IntToText"));

        // The decimal point is the invariant one no matter what the server's locale is.
        Assert.Equal("1234.56", GeneratedAssembly.Get<string>(dto, "DecimalToText"));
        Assert.Equal("True", GeneratedAssembly.Get<string>(dto, "BoolToText"));

        // An enum becomes its NAME, which is what a client can read and send back.
        Assert.Equal("Gold", GeneratedAssembly.Get<string>(dto, "EnumToText"));
        Assert.Equal("7", GeneratedAssembly.Get<string>(dto, "OptionalIntToText"));
    }

    [Fact]
    public void TextToValues_RoundTripBack()
    {
        var sample = MapperGeneratorHarness.Load(Scaffold);
        var mapper = sample.Mapper("Generated_Row_");

        var dto = sample.New("Sample.RowDTO",
            ("TextToInt", 0), ("TextToDecimal", 0m), ("TextToBool", false));

        GeneratedAssembly.Set(dto, "IntToText", "42");
        GeneratedAssembly.Set(dto, "DecimalToText", "1234.56");
        GeneratedAssembly.Set(dto, "DoubleToText", "2.5");
        GeneratedAssembly.Set(dto, "BoolToText", "True");
        GeneratedAssembly.Set(dto, "DateToText", "2026-08-21T13:45:00");
        GeneratedAssembly.Set(dto, "EnumToText", "Gold");
        GeneratedAssembly.Set(dto, "OptionalIntToText", null);

        var saved = mapper.MapToEntity(dto, sample.New("Sample.Row"));

        Assert.Equal(42, GeneratedAssembly.Get<int>(saved, "IntToText"));
        Assert.Equal(1234.56m, GeneratedAssembly.Get<decimal>(saved, "DecimalToText"));
        Assert.True(GeneratedAssembly.Get<bool>(saved, "BoolToText"));
        Assert.Equal(new DateTime(2026, 8, 21, 13, 45, 0), GeneratedAssembly.Get<DateTime>(saved, "DateToText"));

        // Blank optional text is an absent value, not a parse failure.
        Assert.Null(GeneratedAssembly.Get<int?>(saved, "OptionalIntToText"));
    }

    /// <summary>
    /// Text becomes an enum by name or by number, case-insensitively — clients send all three forms.
    /// (The entity stores this one as text and the DTO as the enum, so this is the read direction.)
    /// </summary>
    [Theory]
    [InlineData("Gold")]
    [InlineData("gold")]
    [InlineData("2")]
    public void EnumText_IsParsedByNameOrNumber(string stored)
    {
        var sample = MapperGeneratorHarness.Load(Scaffold);
        var mapper = sample.Mapper("Generated_Row_");

        var entity = sample.New("Sample.Row",
            ("TextToInt", "1"), ("TextToDecimal", "1"), ("TextToBool", "true"),
            ("TextToDate", "2026-01-01"), ("TextToOptionalInt", "1"),
            ("TextToEnum", stored));

        var dto = mapper.MapToView(entity);

        Assert.Equal(2, (int)GeneratedAssembly.Get<object>(dto, "TextToEnum")!);
    }

    /// <summary>
    /// Malformed text throws and names the member, rather than writing a default. A member that quietly becomes
    /// <c>0</c> saves a row that looks fine and is wrong, and nothing reports it.
    /// </summary>
    [Fact]
    public void MalformedText_ThrowsNamingTheMember()
    {
        var sample = MapperGeneratorHarness.Load(Scaffold);
        var mapper = sample.Mapper("Generated_Row_");

        var dto = sample.New("Sample.RowDTO");
        SetRequiredText(dto);
        GeneratedAssembly.Set(dto, "IntToText", "not-a-number");

        var error = Assert.Throws<ShiftEntityMappingException>(
            () => mapper.MapToEntity(dto, sample.New("Sample.Row")));

        Assert.Contains("IntToText", error.Message);
    }

    /// <summary>Numeric widening and narrowing both work, in both directions.</summary>
    [Fact]
    public void NumericTypes_ConvertBothWays()
    {
        var sample = MapperGeneratorHarness.Load(Scaffold);
        var mapper = sample.Mapper("Generated_Row_");

        var entity = sample.New("Sample.Row",
            ("NarrowToInt", 5_000_000_000L),   // long -> int on the DTO
            ("WidenToDecimal", 42),            // int  -> decimal on the DTO
            ("TextToInt", "1"), ("TextToDecimal", "1"), ("TextToBool", "true"),
            ("TextToDate", "2026-01-01"), ("TextToEnum", "Gold"), ("TextToOptionalInt", "1"));

        var dto = mapper.MapToView(entity);

        Assert.Equal(42m, GeneratedAssembly.Get<decimal>(dto, "WidenToDecimal"));

        var saved = mapper.MapToEntity(dto, sample.New("Sample.Row"));

        Assert.Equal(42, GeneratedAssembly.Get<int>(saved, "WidenToDecimal"));
    }

    /// <summary>The text members are required on the entity, so give them something parsable.</summary>
    private static void SetRequiredText(object dto)
    {
        GeneratedAssembly.Set(dto, "IntToText", "1");
        GeneratedAssembly.Set(dto, "DecimalToText", "1");
        GeneratedAssembly.Set(dto, "DoubleToText", "1");
        GeneratedAssembly.Set(dto, "BoolToText", "true");
        GeneratedAssembly.Set(dto, "DateToText", "2026-01-01");
        GeneratedAssembly.Set(dto, "EnumToText", "Gold");
    }
}
