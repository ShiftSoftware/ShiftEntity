using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pins the small correctness fixes from the generator cleanup batch. None of them gates anything; each was a
/// paper cut with a real, if narrow, way of going wrong.
/// </summary>
public class GeneratorCleanupTests
{
    /// <summary>
    /// An init-only DTO member cannot be assigned by the generated mapper — it builds the DTO and then sets
    /// properties, and an init-only setter is closed by then. AutoMapper reached them by reflection, so moving
    /// a DTO across silently lost them. It cannot be fixed, but it can be SAID, and previously it was not even
    /// visible to the unmapped scan.
    /// </summary>
    [Fact]
    public void InitOnlyMember_IsReported()
    {
        var run = MapperGeneratorHarness.Run("""
            using System;
            using ShiftSoftware.ShiftEntity.Core;
            using ShiftSoftware.ShiftEntity.EFCore;
            using ShiftSoftware.ShiftEntity.Model.Dtos;

            namespace Sample;

            public class SampleDb : ShiftDbContext { }

            public class Widget : ShiftEntity<Widget>
            {
                public string Name { get; set; } = "";
                public string Code { get; set; } = "";
            }

            public class WidgetDTO : ShiftEntityViewAndUpsertDTO
            {
                public override string? ID { get; set; }
                public string Name { get; set; } = "";
                public string Code { get; init; } = "";
            }

            public class WidgetRepository : ShiftRepository<SampleDb, Widget, WidgetDTO, WidgetDTO>
            {
                public WidgetRepository(SampleDb db) : base(db) { }
            }
            """);

        var message = Assert.Single(run.OfId("SHENGEN004")).GetMessage();

        Assert.Contains("Code", message);
        Assert.Contains("init-only", message);
    }

    /// <summary>
    /// <c>[ShiftEntityKeyAndName]</c> names the property a select DTO's display text comes from. The generator
    /// used to hardcode "Name", so a type naming a different text property was read from the wrong one — or,
    /// having no "Name" at all, silently got no text.
    /// </summary>
    [Fact]
    public void SelectDtoText_ComesFromTheDeclaredTextProperty()
    {
        var view = MapperGeneratorHarness.Run("""
            using System;
            using ShiftSoftware.ShiftEntity.Core;
            using ShiftSoftware.ShiftEntity.EFCore;
            using ShiftSoftware.ShiftEntity.Model;
            using ShiftSoftware.ShiftEntity.Model.Dtos;

            namespace Sample;

            public class SampleDb : ShiftDbContext { }

            // The display text lives on Title, and the attribute says so.
            [ShiftEntityKeyAndName(nameof(ID), nameof(Title))]
            public class Brand : ShiftEntity<Brand>
            {
                public string Title { get; set; } = "";
            }

            public class Widget : ShiftEntity<Widget>
            {
                public long BrandID { get; set; }
                public Brand Brand { get; set; } = new();
            }

            public class WidgetDTO : ShiftEntityViewAndUpsertDTO
            {
                public override string? ID { get; set; }
                public ShiftEntitySelectDTO Brand { get; set; } = new();
            }

            public class WidgetRepository : ShiftRepository<SampleDb, Widget, WidgetDTO, WidgetDTO>
            {
                public WidgetRepository(SampleDb db) : base(db) { }
            }
            """).Source("Generated_Widget_");

        Assert.Contains("entity.Brand.Title", view);
    }

    /// <summary>
    /// Two same-named pairs in different namespaces generate two same-named classes. The hint name used to be
    /// the class name alone, and duplicate hint names make the generator fail outright rather than emit both.
    /// </summary>
    [Fact]
    public void SameNamedPairsInDifferentNamespaces_BothGenerate()
    {
        var run = MapperGeneratorHarness.Run("""
            using System;
            using System.Collections.Generic;
            using ShiftSoftware.ShiftEntity.Core;
            using ShiftSoftware.ShiftEntity.EFCore;
            using ShiftSoftware.ShiftEntity.Model.Dtos;

            namespace Sample.Left
            {
                public class Part { public string Code { get; set; } = ""; }
                public class PartDTO { public string Code { get; set; } = ""; }
            }

            namespace Sample.Right
            {
                public class Part { public string Code { get; set; } = ""; }
                public class PartDTO { public string Code { get; set; } = ""; }
            }

            namespace Sample
            {
                public class SampleDb : ShiftDbContext { }

                public class Kit : ShiftEntity<Kit>
                {
                    public List<Left.Part> LeftParts { get; set; } = new();
                    public List<Right.Part> RightParts { get; set; } = new();
                }

                public class KitDTO : ShiftEntityViewAndUpsertDTO
                {
                    public override string? ID { get; set; }
                    public List<Left.PartDTO> LeftParts { get; set; } = new();
                    public List<Right.PartDTO> RightParts { get; set; } = new();
                }

                public class KitRepository : ShiftRepository<SampleDb, Kit, KitDTO, KitDTO>
                {
                    public KitRepository(SampleDb db) : base(db) { }
                }
            }
            """);

        Assert.Equal(2, run.Sources.Count(s => s.Name.Contains("Generated_Pair_Part_PartDTO")));
    }
}
