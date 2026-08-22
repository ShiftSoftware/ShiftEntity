using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pins that the generator tells a FRAMEWORK member apart from a DOMAIN member by where it is declared, not by
/// what it is called.
/// <para>
/// The reserved names — <c>ID</c>, <c>Tags</c>, <c>Revisions</c>, the audit fields — used to be matched as bare
/// strings. So an ordinary business column that happened to be called <c>Tags</c> (a comma-separated keyword
/// field, say) was silently dropped from the view, the entity AND the list. It read back null, it never saved,
/// and the build said nothing. That is live data loss, and these tests are what stops it coming back.
/// </para>
/// </summary>
public class FrameworkMemberGatingTests
{
    private const string Scaffold = """
        using System;
        using System.Collections.Generic;
        using ShiftSoftware.ShiftEntity.Core;
        using ShiftSoftware.ShiftEntity.EFCore;
        using ShiftSoftware.ShiftEntity.Model.Dtos;

        namespace Sample;

        public class SampleDb : ShiftDbContext { }

        // Nothing here implements IShiftEntityTaggable. "Tags" and "Revisions" are just column names, the way a
        // domain model is entitled to name them.
        public class Article : ShiftEntity<Article>
        {
            public string Title { get; set; } = "";
            public string Tags { get; set; } = "";
            public string Revisions { get; set; } = "";
        }

        public class ArticleDTO : ShiftEntityViewAndUpsertDTO
        {
            public override string? ID { get; set; }
            public string Title { get; set; } = "";
            public string Tags { get; set; } = "";
            public string Revisions { get; set; } = "";
        }

        public class ArticleListDTO : ShiftEntityListDTO
        {
            public override string? ID { get; set; }
            public string Title { get; set; } = "";
            public string Tags { get; set; } = "";
        }

        public class ArticleRepository : ShiftRepository<SampleDb, Article, ArticleListDTO, ArticleDTO>
        {
            public ArticleRepository(SampleDb db) : base(db) { }
        }
        """;

    /// <summary>Read out, write back: a domain column named <c>Tags</c> is an ordinary string on both legs.</summary>
    [Fact]
    public void DomainColumnsNamedLikeFrameworkMembers_RoundTrip()
    {
        var sample = MapperGeneratorHarness.Load(Scaffold);
        var mapper = sample.Mapper("Generated_Article_");

        var entity = sample.New("Sample.Article",
            ("Title", "On Mapping"), ("Tags", "roslyn,codegen"), ("Revisions", "3"));

        var dto = mapper.MapToView(entity);

        Assert.Equal("roslyn,codegen", GeneratedAssembly.Get<string>(dto, "Tags"));
        Assert.Equal("3", GeneratedAssembly.Get<string>(dto, "Revisions"));

        // The write half is where this bit hardest: the field displayed fine and never persisted.
        var saved = mapper.MapToEntity(dto, sample.New("Sample.Article"));

        Assert.Equal("roslyn,codegen", GeneratedAssembly.Get<string>(saved, "Tags"));
        Assert.Equal("3", GeneratedAssembly.Get<string>(saved, "Revisions"));
    }

    /// <summary>
    /// The list direction had its own hardcoded <c>p.Name != "Tags"</c>, so the column vanished from every grid
    /// too — and <c>Revisions</c> was the same trap waiting for the first entity to use the name.
    /// </summary>
    [Fact]
    public void DomainColumnNamedTags_IsProjectedIntoTheList()
    {
        var sample = MapperGeneratorHarness.Load(Scaffold);
        var mapper = sample.Mapper("Generated_Article_");

        var entity = sample.New("Sample.Article", ("Title", "On Mapping"), ("Tags", "roslyn,codegen"));

        var row = Assert.Single(mapper.MapToList(sample.Queryable("Sample.Article", entity)));

        Assert.Equal("On Mapping", GeneratedAssembly.Get<string>(row, "Title"));
        Assert.Equal("roslyn,codegen", GeneratedAssembly.Get<string>(row, "Tags"));
    }

    /// <summary>
    /// The other half of the contract, and the line moved on 2026-08-22: the audit and soft-delete columns are
    /// PAYLOAD, so the mapper maps them — matching what AutoMapper's unguarded <c>ReverseMap</c> always did.
    /// Deciding who may change them is the repository's job (the upsert restores the stored <c>IsDeleted</c> on
    /// update, so soft delete still needs Access.Delete) or an explicit <c>map.IgnoreEntity(...)</c>.
    /// <para>
    /// What the mapper still refuses to write is what the SAVE PIPELINE owns, and <c>ID</c> is the one that
    /// matters most: <c>EntityConvention</c> would resolve <c>string? → long</c> to <c>ToLong()</c>, which throws
    /// on the null every insert carries, and deep write would push a child's key onto a fresh entity. Anyone
    /// "finishing the job" by removing <c>ID</c> from the set fails right here, before it reaches a database.
    /// </para>
    /// </summary>
    [Fact]
    public void FrameworkAuditMembers_AreWrittenBackToTheEntity_ButTheKeyIsNot()
    {
        var run = MapperGeneratorHarness.Run(Scaffold);
        var entityBody = run.Source("Generated_Article_").Split("MapToEntityGenerated")[1];

        foreach (var payload in new[] { "existing.CreateDate", "existing.LastSaveDate", "existing.IsDeleted" })
            Assert.Contains(payload, entityBody);

        // The key stays pipeline-owned. This assertion is the guard on the carve-out, not an incidental detail.
        Assert.DoesNotContain("existing.ID", entityBody);

        // ...while the identically-shaped domain members right next to them ARE written.
        Assert.Contains("existing.Tags", entityBody);
        Assert.Contains("existing.Revisions", entityBody);
    }
}
