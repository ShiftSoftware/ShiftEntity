using Microsoft.Extensions.DependencyInjection;
using ShiftMapper;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins how <see cref="ReplicationMapper"/> reaches the ShiftMapper mapper a delegate-less replication call maps
/// through, over hand-written <see cref="IShiftMapper"/> doubles. The helper's job is RESOLUTION — the one
/// <see cref="IShiftMapper"/> door <c>AddShiftMapper</c> registers (a mapper, or the <see cref="CompositeShiftMapper"/>
/// it builds over several), and how a bad host configuration is reported; what a real generated mapper produces is
/// pinned by the replication goldens in the template's test project, not here.
/// </summary>
public class ReplicationMapperTests
{
    private sealed class Row { public long ID { get; set; } }
    private sealed class Document { public string? id { get; set; } public string? Tag { get; set; } }

    /// <summary>A mapper double that answers <c>CanMap</c> for one pair and stamps its name on what it maps.</summary>
    private sealed class FakeMapper : IShiftMapper
    {
        private readonly (Type source, Type destination)? pair;

        public FakeMapper(string name, (Type source, Type destination)? pair)
        {
            Name = name;
            this.pair = pair;
        }

        public string Name { get; }

        public bool CanMap(Type source, Type destination) =>
            pair is { } p && p.source == source && p.destination == destination;

        public TDestination Map<TDestination>(object source) => throw new NotSupportedException();

        public TDestination Map<TSource, TDestination>(TSource source) =>
            (TDestination)(object)new Document { id = ((Row)(object)source!).ID.ToString(), Tag = Name };

        public TDestination Map<TSource, TDestination>(TSource source, TDestination destination)
        {
            var document = (Document)(object)destination!;
            document.id = ((Row)(object)source!).ID.ToString();
            document.Tag = Name;
            return destination;
        }

        public IQueryable<TDestination> ProjectTo<TSource, TDestination>(IQueryable<TSource> source) => throw new NotSupportedException();
    }

    /// <summary>
    /// What <c>AddShiftMapper</c> leaves in the container: nothing, the mapper itself, or one composite over all of
    /// them — never several <see cref="IShiftMapper"/> descriptors.
    /// </summary>
    private static IServiceProvider Host(params IShiftMapper[] mappers)
    {
        var services = new ServiceCollection();

        if (mappers.Length == 1)
            services.AddSingleton(mappers[0]);
        else if (mappers.Length > 1)
            services.AddSingleton<IShiftMapper>(new CompositeShiftMapper(mappers));

        return services.BuildServiceProvider();
    }

    [Fact]
    public void NoMapperRegistered_ThrowsOutOfResolution_NamingTheOperationAndTheFix()
    {
        var host = Host();

        var error = Assert.Throws<InvalidOperationException>(() =>
            ReplicationMapper.ResolveCreate<Row, Document>(host, "Replicate<Document>(\"docs\")"));

        Assert.Contains("Replicate<Document>(\"docs\")", error.Message);
        Assert.Contains("no ShiftMapper mapper is registered", error.Message);
        Assert.Contains("AddShiftMapper", error.Message);
        Assert.Contains("CreateMap<Row, Document>()", error.Message);
    }

    [Fact]
    public void RegisteredMapperWithoutThePair_Throws_NamingTheMapperAndThePair()
    {
        var host = Host(new FakeMapper("Other", (typeof(Row), typeof(string))));

        var error = Assert.Throws<InvalidOperationException>(() =>
            ReplicationMapper.ResolveMerge<Row, Document>(host, "UpdateReference<Document>(\"docs\")"));

        Assert.Contains("UpdateReference<Document>(\"docs\")", error.Message);
        Assert.Contains("FakeMapper", error.Message);
        Assert.Contains("'Row' to 'Document'", error.Message);
        Assert.Contains("CreateMap<Row, Document>()", error.Message);
    }

    [Fact]
    public void SeveralMappers_TheCompositeAnswers_AndTheFirstOneDeclaringThePairWins()
    {
        //Three mappers behind the one composite AddShiftMapper registers: the pair is declared by the first and the
        //second, not the third. The helper resolves the composite and asks IT — it does not enumerate mappers or
        //choose between them — and the composite's own rule is first registered wins. (Two mappers each writing
        //their OWN map for a pair is refused by ShiftMapper before any of this runs; two answering here can only be
        //one declaration reached two ways, where either runs the same map.)
        var host = Host(
            new FakeMapper("First", (typeof(Row), typeof(Document))),
            new FakeMapper("Second", (typeof(Row), typeof(Document))),
            new FakeMapper("Third", (typeof(Row), typeof(string))));

        var create = ReplicationMapper.ResolveCreate<Row, Document>(host, "Replicate");

        var document = create(new Row { ID = 7 });

        Assert.Equal("7", document.id);
        Assert.Equal("First", document.Tag);
    }

    [Fact]
    public void SeveralMappersWithoutThePair_Throws_NamingEveryMapperBehindTheComposite()
    {
        //A host reading "CompositeShiftMapper declares no map" would learn nothing; the message has to name the
        //mappers the host actually registered.
        var host = Host(
            new FakeMapper("First", (typeof(Row), typeof(string))),
            new FakeMapper("Second", (typeof(Row), typeof(int))));

        var error = Assert.Throws<InvalidOperationException>(() =>
            ReplicationMapper.ResolveCreate<Row, Document>(host, "Replicate<Document>(\"docs\")"));

        Assert.Contains("none of the 2 registered", error.Message);
        Assert.Contains("FakeMapper, FakeMapper", error.Message);
        Assert.DoesNotContain("CompositeShiftMapper", error.Message);
        Assert.Contains("'Row' to 'Document'", error.Message);
    }

    [Fact]
    public void ResolveMerge_MapsOntoTheStoredDocumentAndHandsTheSameInstanceBack()
    {
        var host = Host(new FakeMapper("Only", (typeof(Row), typeof(Document))));

        var merge = ReplicationMapper.ResolveMerge<Row, Document>(host, "UpdateReference");

        var existing = new Document { id = "stale", Tag = "stale" };
        var merged = merge(new Row { ID = 42 }, existing);

        Assert.Same(existing, merged);
        Assert.Equal("42", merged.id);
        Assert.Equal("Only", merged.Tag);
    }
}
