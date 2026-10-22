using System.Linq.Expressions;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.TypeAuth.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// The row check compiles a <c>Match</c> dimension's expression once, with the accessible ids as a parameter, instead
/// of building and compiling it on every check. The answers must stay exactly what compiling <c>Match(ids)</c> per call
/// gives, including for factories that cannot be compiled once.
/// </summary>
public class MatchCompilerTests
{
    public sealed class Item
    {
        public long? A { get; set; }
        public long? B { get; set; }
        public List<long?> Children { get; set; } = [];
    }

    private static readonly IReadOnlyCollection<long?>[] IdSets =
    [
        Array.Empty<long?>(),
        new long?[] { null },
        new long?[] { 1 },
        new long?[] { 1, 2, null },
        new List<long?> { 3, 7 },
    ];

    private static readonly Item[] Items =
    [
        new() { A = 1, B = 3 },
        new() { A = null, B = 2, Children = [7] },
        new() { A = 7, B = null, Children = [1, null] },
        new() { A = 5, B = 6 },
    ];

    // A captured variable that changes after the dimension is declared. The expression reads it when it runs.
    private static long? threshold = 4;

    public static TheoryData<string> Factories => new(FactoriesByName.Keys);

    private static readonly Dictionary<string, DataLevelMatch<Item>> FactoriesByName = new()
    {
        ["one column"] = ids => item => ids.Contains(item.A),
        ["two columns"] = ids => item => ids.Contains(item.A) || ids.Contains(item.B),
        ["child collection"] = ids => item => item.Children.Any(child => ids.Contains(child)),
        ["count inside the expression"] = ids => item => ids.Count > 1 && ids.Contains(item.A),
        ["captured variable"] = ids => item => ids.Contains(item.A) || item.B > threshold,
        // These compute something from the ids outside the expression, so they are compiled per call.
        ["set built outside"] = ids => BuildFromSet(ids.Where(id => id is not null).ToHashSet()),
        ["count taken outside"] = ids => CountedOutside(ids.Count),
        ["first id read outside"] = ids => FirstOutside(ids.First()),
    };

    private static Expression<Func<Item, bool>> BuildFromSet(HashSet<long?> set) => item => set.Contains(item.A);
    private static Expression<Func<Item, bool>> CountedOutside(int count) => item => count > 1;
    private static Expression<Func<Item, bool>> FirstOutside(long? first) => item => item.A == first;

    [Theory]
    [MemberData(nameof(Factories))]
    public void The_compiled_match_answers_like_the_expression_compiled_per_call(string name)
    {
        var factory = FactoriesByName[name];
        var predicate = new MatchPredicate<Item>(factory);

        foreach (var ids in IdSets)
            foreach (var item in Items)
            {
                bool expected;
                try { expected = factory(ids).Compile()(item); }
                catch (InvalidOperationException) { continue; } // "first id read outside" on an empty set

                Assert.Equal(expected, predicate.Evaluate(item, ids));
            }

        // A captured variable is read when the predicate runs, not when it is compiled.
        threshold = 10;
        try
        {
            foreach (var ids in IdSets.Skip(1))
                foreach (var item in Items)
                    Assert.Equal(factory(ids).Compile()(item), predicate.Evaluate(item, ids));
        }
        finally
        {
            threshold = 4;
        }
    }

    [Theory]
    [InlineData("one column", true)]
    [InlineData("two columns", true)]
    [InlineData("child collection", true)]
    [InlineData("count inside the expression", true)]
    [InlineData("captured variable", true)]
    [InlineData("set built outside", false)]
    [InlineData("count taken outside", false)]
    [InlineData("first id read outside", false)]
    public void Only_a_factory_that_reads_the_ids_inside_its_expression_is_compiled_once(string name, bool compiledOnce)
        => Assert.Equal(compiledOnce, MatchCompiler.TryCompile(FactoriesByName[name]) is not null);

    [Fact]
    public void A_match_dimension_is_compiled_once_for_every_row_check()
    {
        var calls = 0;
        var builder = new DataLevelAccessBuilder<Vehicle>();
        builder.On(VehicleDataLevel.Companies).Match(ids =>
        {
            calls++;
            return vehicle => ids.Contains(vehicle.CompanyID);
        });
        var policy = new DataLevelAccessPolicy<Vehicle>(builder);
        var vehicles = VehicleScenario.SampleVehicles();

        var allowed = new List<(long Company, long Vehicle)>();
        foreach (var company in new long[] { Companies.DealerA, Companies.DealerB, Companies.Intermediary })
            foreach (var vehicle in vehicles)
                if (policy.Authorize(vehicle, Access.Read, Context(ScopedTypeAuth.ToCompany(company))))
                    allowed.Add((company, vehicle.Id));

        // Two calls build the compiled predicate; no row check calls the factory after that.
        Assert.Equal(2, calls);
        Assert.Equal([(Companies.DealerA, 1L), (Companies.DealerA, 4L), (Companies.DealerB, 2L), (Companies.Intermediary, 3L)], allowed);

        // The query path still calls the factory per query: Entity Framework needs the ids inside the expression.
        policy.ApplyQueryFilter(vehicles.AsQueryable(), Access.Read, Context(ScopedTypeAuth.ToCompany(Companies.DealerA)));
        Assert.Equal(3, calls);
    }

    private static DataLevelAccessContext Context(TypeAuthContext typeAuth)
        => new(new TypeAuthAccessibleItemsSource(typeAuth), FakeCurrentUserProvider.Anonymous(), new RecordingHashIdService());
}
