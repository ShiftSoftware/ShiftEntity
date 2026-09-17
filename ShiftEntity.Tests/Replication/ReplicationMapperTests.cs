using Microsoft.Extensions.DependencyInjection;
using ShiftMapper;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins how <see cref="ReplicationMapper"/> reaches the ShiftMapper mapper a delegate-less replication call maps
/// through, over hand-written <see cref="IMapper"/> doubles. The helper's job is RESOLUTION — the one
/// <see cref="IMapper"/> door <c>AddShiftMapper</c> registers (the <see cref="Mapper"/> over every generated mapper
/// registered), and how a bad host configuration is reported; which generated mapper answers for a pair is
/// <see cref="Mapper"/>'s own rule, pinned in ShiftMapper's tests, and what a real generated mapper produces is
/// pinned by the replication goldens in the template's test project, not here.
/// </summary>
public class ReplicationMapperTests
{
    private sealed class Row { public long ID { get; set; } }
    private sealed class Document { public string? id { get; set; } public string? Tag { get; set; } }

    /// <summary>A mapper double that answers <c>CanMap</c> for one pair and stamps its name on what it maps.</summary>
    private sealed class FakeMapper : IMapper
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

    /// <summary>What the helper sees in the container: nothing, or the one <see cref="IMapper"/> door.</summary>
    private static IServiceProvider Host(IMapper? mapper = null)
    {
        var services = new ServiceCollection();

        if (mapper is not null)
            services.AddSingleton(mapper);

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
    public void MapperWithNothingRegistered_IsReportedAsNoMapper_NotAsShiftMappersOwnCreateAdvice()
    {
        //A bare Mapper is the door AddShiftMapper leaves behind when only an assembly with no maps called it. Its
        //own CanMap throws, talking about Mapper.Create — advice for a test, not for a host that forgot to declare
        //the pair. The helper reads the same situation as "nothing registered" and says what to do about it.
        var host = Host(new Mapper());

        var error = Assert.Throws<InvalidOperationException>(() =>
            ReplicationMapper.ResolveCreate<Row, Document>(host, "Replicate<Document>(\"docs\")"));

        Assert.Contains("no ShiftMapper mapper is registered", error.Message);
        Assert.Contains("AddShiftMapper", error.Message);
        Assert.DoesNotContain("Mapper.Create", error.Message);
    }

    [Fact]
    public void RegisteredMapperWithoutThePair_Throws_NamingTheMapperAndThePair()
    {
        var host = Host(new FakeMapper("Other", (typeof(Row), typeof(string))));

        var error = Assert.Throws<InvalidOperationException>(() =>
            ReplicationMapper.ResolveMerge<Row, Document>(host, "UpdateReference<Document>(\"docs\")"));

        Assert.Contains("UpdateReference<Document>(\"docs\")", error.Message);
        Assert.Contains("none of the 1 registered", error.Message);
        Assert.Contains("FakeMapper", error.Message);
        Assert.Contains("'Row' to 'Document'", error.Message);
        Assert.Contains("CreateMap<Row, Document>()", error.Message);
    }

    [Fact]
    public void ResolveCreate_AsksTheOneDoorAndMapsThroughIt()
    {
        //The helper resolves IMapper and asks IT — it does not enumerate generated mappers or choose between them;
        //that is Mapper's job, and a pair two of them declare through ONE declaration runs the same map whichever
        //answers.
        var host = Host(new FakeMapper("Only", (typeof(Row), typeof(Document))));

        var create = ReplicationMapper.ResolveCreate<Row, Document>(host, "Replicate");

        var document = create(new Row { ID = 7 });

        Assert.Equal("7", document.id);
        Assert.Equal("Only", document.Tag);
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
