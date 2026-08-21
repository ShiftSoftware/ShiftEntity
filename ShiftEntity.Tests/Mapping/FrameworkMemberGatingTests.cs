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
    /// The other half of the contract: a REAL framework member still has to be left to the framework. The audit
    /// fields belong to the save pipeline, and a mapper that wrote them from client input would let a caller set
    /// its own <c>IsDeleted</c> — so the gate must not become a free-for-all.
    /// </summary>
    [Fact]
    public void RealFrameworkMembers_AreStillNotWrittenBackToTheEntity()
    {
        var run = MapperGeneratorHarness.Run(Scaffold);
        var entityBody = run.Source("Generated_Article_").Split("MapToEntityGenerated")[1];

        foreach (var owned in new[] { "existing.ID", "existing.CreateDate", "existing.LastSaveDate", "existing.IsDeleted" })
            Assert.DoesNotContain(owned, entityBody);

        // ...while the identically-shaped domain members right next to them ARE written.
        Assert.Contains("existing.Tags", entityBody);
        Assert.Contains("existing.Revisions", entityBody);
    }
}
