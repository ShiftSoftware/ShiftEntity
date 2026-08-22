using System;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pins the 2026-08-22 decision (open question Q7): generated <c>MapToEntity</c> WRITES the audit and soft-delete
/// columns from the DTO, exactly as AutoMapper's unguarded <c>ReverseMap</c> always did. The mapper's job is to
/// map; deciding who may change a value belongs to the repository or to an explicit <c>map.IgnoreEntity(...)</c>.
/// <para>
/// The carve-out is <c>ID</c>. It is not a policy exception — <c>EntityConvention</c> resolves entity
/// <c>long ID</c> from DTO <c>string? ID</c> through <c>ToLong()</c>, which throws on the null that every insert
/// carries, and deep write would push a child's key onto a freshly constructed entity. Writing it needs a
/// null-tolerant convention that does not exist yet, so it stays pipeline-owned and is pinned below.
/// </para>
/// </summary>
public class FrameworkAuditMemberWriteTests
{
    private const string Scaffold = """
        using System;
        using System.Collections.Generic;
        using ShiftSoftware.ShiftEntity.Core;
        using ShiftSoftware.ShiftEntity.EFCore;
        using ShiftSoftware.ShiftEntity.Model.Dtos;

        namespace Sample;

        public class SampleDb : ShiftDbContext { }

        public class Invoice : ShiftEntity<Invoice>
        {
            public string Number { get; set; } = "";
        }

        public class InvoiceDTO : ShiftEntityViewAndUpsertDTO
        {
            public override string? ID { get; set; }
            public string Number { get; set; } = "";
        }

        public class InvoiceListDTO : ShiftEntityListDTO
        {
            public override string? ID { get; set; }
            public string Number { get; set; } = "";
        }

        public class InvoiceRepository : ShiftRepository<SampleDb, Invoice, InvoiceListDTO, InvoiceDTO>
        {
            public InvoiceRepository(SampleDb db) : base(db) { }
        }
        """;

    // A — the contract this change exists to create.
    [Fact]
    public void AuditAndSoftDeleteColumns_AreWrittenFromTheDto()
    {
        var sample = MapperGeneratorHarness.Load(Scaffold);
        var mapper = sample.Mapper("Generated_Invoice_");

        var created = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var saved = new DateTimeOffset(2021, 6, 7, 8, 9, 10, TimeSpan.Zero);

        var dto = sample.New("Sample.InvoiceDTO",
            ("Number", "INV-1"),
            ("CreateDate", created),
            ("LastSaveDate", saved),
            ("IsDeleted", true),
            ("CreatedByUserID", "42"),
            ("LastSavedByUserID", "43"));

        var entity = mapper.MapToEntity(dto, sample.New("Sample.Invoice"));

        Assert.Equal(created, GeneratedAssembly.Get<DateTimeOffset>(entity, "CreateDate"));
        Assert.Equal(saved, GeneratedAssembly.Get<DateTimeOffset>(entity, "LastSaveDate"));
        Assert.True(GeneratedAssembly.Get<bool>(entity, "IsDeleted"));
        Assert.Equal(42L, GeneratedAssembly.Get<long?>(entity, "CreatedByUserID"));
        Assert.Equal(43L, GeneratedAssembly.Get<long?>(entity, "LastSavedByUserID"));
    }

    // B — the guard on the carve-out. This fails the instant someone "finishes the job" and removes "ID" from
    // EntityExcludedMembers, which would 500 every POST in every consuming app.
    [Fact]
    public void TheKey_IsStillNotWrittenFromTheDto()
    {
        var sample = MapperGeneratorHarness.Load(Scaffold);
        var mapper = sample.Mapper("Generated_Invoice_");

        var dto = sample.New("Sample.InvoiceDTO", ("ID", "7"), ("Number", "INV-1"));

        var entity = mapper.MapToEntity(dto, sample.New("Sample.Invoice"));

        Assert.Equal(0L, GeneratedAssembly.Get<long>(entity, "ID"));
    }

    // C — the difference between the five and ID, stated rather than inferred. A create request carries neither
    // user id; ToNullableLong tolerates that, ToLong (which ID would use) does not.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BlankUserIds_MapToNull_RatherThanThrowing(string? value)
    {
        var sample = MapperGeneratorHarness.Load(Scaffold);
        var mapper = sample.Mapper("Generated_Invoice_");

        var dto = sample.New("Sample.InvoiceDTO",
            ("Number", "INV-1"), ("CreatedByUserID", value), ("LastSavedByUserID", value));

        var entity = mapper.MapToEntity(dto, sample.New("Sample.Invoice"));

        Assert.Null(GeneratedAssembly.Get<long?>(entity, "CreatedByUserID"));
        Assert.Null(GeneratedAssembly.Get<long?>(entity, "LastSavedByUserID"));
    }

    // D — the escape hatch the decision's rationale rests on. If IgnoreEntity could not reach a framework member,
    // "the programmer can opt out" would be false and the whole line would have to move back into the generator.
    [Fact]
    public void IgnoreEntity_SuppressesAFrameworkMember()
    {
        const string withIgnore = """
            using System;
            using ShiftSoftware.ShiftEntity.Core;
            using ShiftSoftware.ShiftEntity.EFCore;
            using ShiftSoftware.ShiftEntity.Model.Dtos;

            namespace Sample;

            public class SampleDb : ShiftDbContext { }

            public class Note : ShiftEntity<Note> { public string Body { get; set; } = ""; }

            public class NoteDTO : ShiftEntityViewAndUpsertDTO
            {
                public override string? ID { get; set; }
                public string Body { get; set; } = "";
            }

            public class NoteListDTO : ShiftEntityListDTO
            {
                public override string? ID { get; set; }
                public string Body { get; set; } = "";
            }

            public class NoteRepository : ShiftRepository<SampleDb, Note, NoteListDTO, NoteDTO>
            {
                public NoteRepository(SampleDb db) : base(db, r => r.UseGeneratedMapper(map => map
                    .IgnoreEntity(e => e.IsDeleted))) { }
            }
            """;

        var run = MapperGeneratorHarness.Run(withIgnore);
        var entityBody = run.Source("Generated_Note_").Split("MapToEntityGenerated")[1];

        Assert.DoesNotContain("existing.IsDeleted", entityBody);
        Assert.Contains("existing.Body", entityBody);
    }

    // E — Tags must stay inert on the write side. TaggingPipeline owns both legs: it runs after MapToEntity and
    // clears the navigation, so a generated Tag composition is discarded work, and a null Tags payload would NRE.
    // Removing "Tags" from EntityExcludedMembers alone emits byte-identical code (pair discovery skips it first),
    // so this guards the scenario where someone also touches ViewHandledMembers and arms it by accident.
    [Fact]
    public void TaggingNavigation_IsNeverWrittenFromTheDto()
    {
        const string taggable = """
            using System.Collections.Generic;
            using ShiftSoftware.ShiftEntity.Core;
            using ShiftSoftware.ShiftEntity.Core.Tagging;
            using ShiftSoftware.ShiftEntity.EFCore;
            using ShiftSoftware.ShiftEntity.Model.Dtos;
            using ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;

            namespace Sample;

            public class SampleDb : ShiftDbContext { }

            public class Product : ShiftEntity<Product>, IShiftEntityTaggable
            {
                public string Name { get; set; } = "";
                public ICollection<Tag> Tags { get; set; } = new List<Tag>();
            }

            public class ProductDTO : ShiftEntityViewAndUpsertDTO, IShiftEntityTaggableDTO
            {
                public override string? ID { get; set; }
                public string Name { get; set; } = "";
                public List<TagDTO>? Tags { get; set; }
            }

            public class ProductListDTO : ShiftEntityListDTO, IShiftEntityTaggableDTO
            {
                public override string? ID { get; set; }
                public string Name { get; set; } = "";
                public List<TagDTO>? Tags { get; set; }
            }

            public class ProductRepository : ShiftRepository<SampleDb, Product, ProductListDTO, ProductDTO>
            {
                public ProductRepository(SampleDb db) : base(db) { }
            }
            """;

        var run = MapperGeneratorHarness.Run(taggable);
        var source = run.Source("Generated_Product_");
        var entityBody = source.Split("MapToEntityGenerated")[1];

        Assert.DoesNotContain("existing.Tags", entityBody);
        Assert.Contains("existing.Name", entityBody);

        // B6a: the tags-in-list splice must target ShiftEntity.CORE. The generator ships inside Core, so emitting
        // a call into ShiftEntity.EFCore meant a Core-only project with a taggable entity got source that would
        // not compile. An [Obsolete] forwarder still lives in EFCore for mappers baked into older packages.
        Assert.Contains("global::ShiftSoftware.ShiftEntity.Core.Tagging.TaggableProjectionExtensions", source);
        Assert.DoesNotContain("global::ShiftSoftware.ShiftEntity.EFCore.Tagging.TaggableProjectionExtensions", source);
    }
}
