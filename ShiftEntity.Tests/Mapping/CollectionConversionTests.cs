using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pins collection mapping for SIMPLE element types, where the container type and/or the element type differ.
/// <para>
/// Complex children were already composed through pair mappers. Collections of plain values were not: the only
/// thing that worked was an exact type match, which was then assigned by reference. A different container
/// (<c>ICollection&lt;int&gt;</c> to <c>List&lt;int&gt;</c>) or a convertible element
/// (<c>List&lt;int&gt;</c> to <c>List&lt;string&gt;</c>) produced no assignment at all — the collection came
/// back empty and never saved.
/// </para>
/// <para>
/// Container adaptation also fixes two older holes: arrays were not recognised as collections in any
/// direction, and everything was materialised with <c>ToList()</c> regardless of target, so an entity with a
/// <c>HashSet</c> navigation generated code that did not compile.
/// </para>
/// </summary>
public class CollectionConversionTests
{
    private const string Scaffold = """
        using System;
        using System.Collections.Generic;
        using ShiftSoftware.ShiftEntity.Core;
        using ShiftSoftware.ShiftEntity.EFCore;
        using ShiftSoftware.ShiftEntity.Model.Dtos;

        namespace Sample;

        public class Bag : ShiftEntity<Bag>
        {
            public ICollection<int> ContainerChanges { get; set; } = new List<int>();
            public List<int> ElementChanges { get; set; } = new();
            public List<string> TextToNumbers { get; set; } = new();
            public HashSet<int> IntoHashSet { get; set; } = new();
            public int[] ArrayToList { get; set; } = Array.Empty<int>();
            public List<int> ListToArray { get; set; } = new();
        }

        public class BagDTO : ShiftEntityViewAndUpsertDTO
        {
            public override string? ID { get; set; }
            public List<int> ContainerChanges { get; set; } = new();       // ICollection<int> -> List<int>
            public List<string> ElementChanges { get; set; } = new();      // List<int>        -> List<string>
            public List<long> TextToNumbers { get; set; } = new();         // List<string>     -> List<long>
            public List<int> IntoHashSet { get; set; } = new();            // HashSet<int>     -> List<int>
            public List<int> ArrayToList { get; set; } = new();            // int[]            -> List<int>
            public int[] ListToArray { get; set; } = Array.Empty<int>();   // List<int>        -> int[]
        }

        public class SampleDb : ShiftDbContext { }

        public class BagRepository : ShiftRepository<SampleDb, Bag, BagDTO, BagDTO>
        {
            public BagRepository(SampleDb db) : base(db) { }
        }
        """;

    /// <summary>Every shape above maps, and maps back. None of them did before.</summary>
    [Fact]
    public void AllShapes_AreMappedAndSymmetric()
    {
        var run = MapperGeneratorHarness.Run(Scaffold);

        Assert.Empty(run.OfId("SHENGEN004"));
        Assert.Empty(run.OfId("SHENGEN008"));
    }

    [Fact]
    public void ContainerAndElementConversions_RoundTrip()
    {
        var sample = MapperGeneratorHarness.Load(Scaffold);
        var mapper = sample.Mapper("Generated_Bag_");

        var entity = sample.New("Sample.Bag",
            ("ContainerChanges", new List<int> { 1, 2 }),
            ("ElementChanges", new List<int> { 7, 8 }),
            ("TextToNumbers", new List<string> { "10", "20" }),
            ("IntoHashSet", new HashSet<int> { 5 }),
            ("ArrayToList", new[] { 3, 4 }),
            ("ListToArray", new List<int> { 9 }));

        var dto = mapper.MapToView(entity);

        Assert.Equal([1, 2], GeneratedAssembly.Get<List<int>>(dto, "ContainerChanges"));

        // Each element converted, not just the container.
        Assert.Equal(["7", "8"], GeneratedAssembly.Get<List<string>>(dto, "ElementChanges"));
        Assert.Equal([10L, 20L], GeneratedAssembly.Get<List<long>>(dto, "TextToNumbers"));
        Assert.Equal([5], GeneratedAssembly.Get<List<int>>(dto, "IntoHashSet"));
        Assert.Equal([3, 4], GeneratedAssembly.Get<List<int>>(dto, "ArrayToList"));
        Assert.Equal([9], GeneratedAssembly.Get<int[]>(dto, "ListToArray"));

        // ── and back ──
        var saved = mapper.MapToEntity(dto, sample.New("Sample.Bag"));

        Assert.Equal([1, 2], GeneratedAssembly.Get<ICollection<int>>(saved, "ContainerChanges"));
        Assert.Equal([7, 8], GeneratedAssembly.Get<List<int>>(saved, "ElementChanges"));
        Assert.Equal(["10", "20"], GeneratedAssembly.Get<List<string>>(saved, "TextToNumbers"));

        // The write side materialises into the target's OWN container type, not always a List.
        Assert.Equal([5], GeneratedAssembly.Get<HashSet<int>>(saved, "IntoHashSet"));
        Assert.Equal([3, 4], GeneratedAssembly.Get<int[]>(saved, "ArrayToList"));
        Assert.Equal([9], GeneratedAssembly.Get<List<int>>(saved, "ListToArray"));
    }

    /// <summary>
    /// A <c>HashSet</c> navigation of COMPLEX children used to emit <c>existing.Kids = ToList(...)</c>, which
    /// does not compile (CS0029). Loading the scaffold is the assertion: the harness compiles what it generated.
    /// </summary>
    [Fact]
    public void HashSetOfComplexChildren_GeneratesCompilableCode()
    {
        var sample = MapperGeneratorHarness.Load("""
            using System;
            using System.Collections.Generic;
            using ShiftSoftware.ShiftEntity.Core;
            using ShiftSoftware.ShiftEntity.EFCore;
            using ShiftSoftware.ShiftEntity.Model.Dtos;

            namespace Sample;

            public class SampleDb : ShiftDbContext { }

            public class Kid { public string Name { get; set; } = ""; }
            public class KidDTO { public string Name { get; set; } = ""; }

            public class Parent : ShiftEntity<Parent>
            {
                public HashSet<Kid> Kids { get; set; } = new();
                public Kid[] Spares { get; set; } = Array.Empty<Kid>();
            }

            public class ParentDTO : ShiftEntityViewAndUpsertDTO
            {
                public override string? ID { get; set; }
                public List<KidDTO> Kids { get; set; } = new();
                public KidDTO[] Spares { get; set; } = Array.Empty<KidDTO>();
            }

            public class ParentRepository : ShiftRepository<SampleDb, Parent, ParentDTO, ParentDTO>
            {
                public ParentRepository(SampleDb db) : base(db) { }
            }
            """);

        var mapper = sample.Mapper("Generated_Parent_");

        var entity = sample.New("Sample.Parent");
        var kids = (System.Collections.IEnumerable)GeneratedAssembly.Get(entity, "Kids")!;
        ((dynamic)kids).Add((dynamic)sample.New("Sample.Kid", ("Name", "Ada")));

        var dto = mapper.MapToView(entity);
        Assert.Equal("Ada", GeneratedAssembly.Get<string>(Assert.Single(GeneratedAssembly.Items(dto, "Kids")), "Name"));

        var saved = mapper.MapToEntity(dto, sample.New("Sample.Parent"));
        Assert.Single(GeneratedAssembly.Items(saved, "Kids"));
    }

    /// <summary>
    /// The list projection has to stay SQL-translatable. Element conversion and container adaptation are both
    /// expressible in SQL; parsing text is not, and is left to <c>SHENGEN007</c> to report rather than emitted
    /// as a projection that would fail at query time.
    /// </summary>
    [Fact]
    public void ListProjection_ConvertsWhatSqlCanAndReportsWhatItCannot()
    {
        var run = MapperGeneratorHarness.Run("""
            using System;
            using System.Collections.Generic;
            using ShiftSoftware.ShiftEntity.Core;
            using ShiftSoftware.ShiftEntity.EFCore;
            using ShiftSoftware.ShiftEntity.Model.Dtos;

            namespace Sample;

            public class SampleDb : ShiftDbContext { }

            public class Bag : ShiftEntity<Bag>
            {
                public int Number { get; set; }
                public string Text { get; set; } = "";
            }

            public class BagDTO : ShiftEntityViewAndUpsertDTO
            {
                public override string? ID { get; set; }
                public string Number { get; set; } = "";
                public int Text { get; set; }
            }

            public class BagListDTO : ShiftEntityListDTO
            {
                public override string? ID { get; set; }
                public string Number { get; set; } = "";   // int -> string: a SQL CAST
                public int Text { get; set; }              // string -> int: no trustworthy SQL equivalent
            }

            public class BagRepository : ShiftRepository<SampleDb, Bag, BagListDTO, BagDTO>
            {
                public BagRepository(SampleDb db) : base(db) { }
            }
            """);

        var listWarning = Assert.Single(run.OfId("SHENGEN007")).GetMessage();

        Assert.Contains("Text", listWarning);
        Assert.DoesNotContain("Number", listWarning);

        // ...and no helper call leaked into the projection, which would fail at query time.
        var projection = run.Source("Generated_Bag_").Split("__shiftListProjection")[1];
        Assert.DoesNotContain("MappingHelpers.ToValue", projection);
    }
}
