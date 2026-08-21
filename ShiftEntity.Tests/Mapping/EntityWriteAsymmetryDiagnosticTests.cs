using Microsoft.CodeAnalysis;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pins <c>SHENGEN008</c> — DTO members that <c>MapToView</c> reads and <c>MapToEntity</c> never writes back.
/// <para>
/// This is the failure the whole AutoMapper-removal effort is built around: the field displays correctly, the
/// save returns 200, and the column never changes. Nothing caught it before — the write direction had no
/// unmapped channel at all.
/// </para>
/// <para>
/// It is deliberately NOT a mirror of <c>SHENGEN004</c>. The entity body walks ENTITY properties, so mirroring
/// would warn about every internal and computed column and be ignored within a day. The asymmetry is the
/// actionable set, and it is the framework's own read/write symmetry rule written down.
/// </para>
/// </summary>
public class EntityWriteAsymmetryDiagnosticTests
{
    private const string Asymmetry = "SHENGEN008";

    /// <summary>
    /// <c>Nickname</c> is read from the entity and — because the entity property has no setter — never written
    /// back. Reads fine, saves nothing.
    /// </summary>
    [Fact]
    public void MemberReadButNeverWritten_Warns()
    {
        var diagnostic = Assert.Single(Run(
            entityExtra: "public string Nickname { get; } = \"\";",
            dtoExtra: "public string Nickname { get; set; } = \"\";"));

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);

        var message = diagnostic.GetMessage();
        Assert.Contains("Nickname", message);

        // The message has to say what to do about it, or it is just an accusation.
        Assert.Contains("IgnoreEntity", message);
    }

    /// <summary>An ordinary read/write member is symmetric and must stay silent.</summary>
    [Fact]
    public void SymmetricMember_IsSilent() => AssertSilent(
        entityExtra: "public string Nickname { get; set; } = \"\";",
        dtoExtra: "public string Nickname { get; set; } = \"\";");

    /// <summary>
    /// <c>IgnoreEntity</c> is how a genuinely read-only DTO member is declared. That is the intended escape
    /// hatch, and it turns each legitimate asymmetry into an explicit, reviewed decision.
    /// </summary>
    [Fact]
    public void IgnoreEntityMember_IsSilent() => AssertSilent(
        entityExtra: "public string Nickname { get; } = \"\";",
        dtoExtra: "public string Nickname { get; set; } = \"\";",
        mapper: """
            [ShiftEntityMapper]
            public partial class PersonMapper : IShiftEntityMapper<Person, PersonDTO, PersonDTO>
            {
                partial void Configure(ShiftMapperBuilder<Person, PersonDTO, PersonDTO> map)
                {
                    map.IgnoreEntity(e => e.Nickname);
                }
            }
            """);

    /// <summary>Same decision, spelled on the property.</summary>
    [Fact]
    public void AttributeIgnoredMember_IsSilent() => AssertSilent(
        entityExtra: "public string Nickname { get; } = \"\";",
        dtoExtra: "[ShiftEntityMapperIgnore] public string Nickname { get; set; } = \"\";");

    /// <summary>
    /// The audit fields are read into the DTO by MapBaseFields and deliberately never written back — that
    /// narrowing is a security property, not a bug. Warning about them would fire on every mapper in existence.
    /// </summary>
    [Fact]
    public void FrameworkAuditFields_AreNeverReported() =>
        AssertSilent(entityExtra: "", dtoExtra: "");

    // ──────────────────────────────── harness ────────────────────────────────

    private static string Scaffold(string entityExtra, string dtoExtra, string mapper) => $$"""
        using System;
        using System.Collections.Generic;
        using ShiftSoftware.ShiftEntity.Core;
        using ShiftSoftware.ShiftEntity.EFCore;
        using ShiftSoftware.ShiftEntity.Model.Dtos;

        namespace Sample;

        public class SampleDb : ShiftDbContext { }

        public class Person : ShiftEntity<Person>
        {
            public string Name { get; set; } = "";
            {{entityExtra}}
        }

        public class PersonDTO : ShiftEntityViewAndUpsertDTO
        {
            public override string? ID { get; set; }
            public string Name { get; set; } = "";
            {{dtoExtra}}
        }

        {{mapper}}

        public class PersonRepository : ShiftRepository<SampleDb, Person, PersonDTO, PersonDTO>
        {
            public PersonRepository(SampleDb db) : base(db) { }
        }
        """;

    private static void AssertSilent(string entityExtra, string dtoExtra, string mapper = "") =>
        Assert.Empty(Run(entityExtra, dtoExtra, mapper));

    private static IEnumerable<Diagnostic> Run(string entityExtra, string dtoExtra, string mapper = "") =>
        MapperGeneratorHarness.Run(Scaffold(entityExtra, dtoExtra, mapper)).OfId(Asymmetry);
}
