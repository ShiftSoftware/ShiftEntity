using Microsoft.Extensions.DependencyInjection;
using ShiftMapper;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins how <see cref="ReplicationMapper"/> picks the ShiftMapper mapper a delegate-less replication call maps
/// through, over hand-written <see cref="IShiftMapper"/> doubles. The helper's job is RESOLUTION — which mapper,
/// and how a bad host configuration is reported; what a real generated mapper produces is pinned by the
/// replication goldens in the template's test project, not here.
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

    private static IServiceProvider Host(params IShiftMapper[] mappers)
    {
        var services = new ServiceCollection();

        foreach (var mapper in mappers)
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
    public void SeveralMappers_TheLastOneDeclaringThePairWins()
    {
        //Three registrations: the pair is declared by the first and the second, not the third. The container's own
        //rule for IShiftMapper is "last wins", and the helper follows it AMONG the mappers that can actually map
        //the pair — so a framework mapper registered after the application's does not shadow a pair only the
        //application's declares, and vice versa.
        var host = Host(
            new FakeMapper("First", (typeof(Row), typeof(Document))),
            new FakeMapper("Second", (typeof(Row), typeof(Document))),
            new FakeMapper("Third", (typeof(Row), typeof(string))));

        var create = ReplicationMapper.ResolveCreate<Row, Document>(host, "Replicate");

        var document = create(new Row { ID = 7 });

        Assert.Equal("7", document.id);
        Assert.Equal("Second", document.Tag);
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
