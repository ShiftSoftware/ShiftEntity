using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.ShiftEntity.Web.Services;
using ShiftSoftware.ShiftIdentity.Core;
using ShiftSoftware.TypeAuth.Core;
using Xunit;
using Constants = ShiftSoftware.ShiftEntity.Core.Constants;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// Verifies the existing APIs a reverse-access consumer can compose without an HTTP request.
/// These tests start with supplied grants and claims; they do not establish replication freshness,
/// subject lifecycle rules, or authorization performed outside the repository's data policy.
/// </summary>
public class ReverseAccessCompositionTests
{
    private const string SystemGrant = """
        {"ShiftIdentityActions":{"Users":["Read"]}}
        """;

    private const string CompanyGrant = """
        {"ShiftIdentityActions":{"DataLevelAccess":{"Companies":{"4":["Read"]}}}}
        """;

    private const string BranchGrant = """
        {"ShiftIdentityActions":{"DataLevelAccess":{"Branches":{"8":["Read"]}}}}
        """;

    private static TypeAuthContext Subject(params string[] trees)
    {
        var builder = new TypeAuthContextBuilder().AddActionTree<ShiftIdentityActions>();
        foreach (var tree in trees)
            builder.AddAccessTree(tree);
        return builder.Build();
    }

    private static DefaultDataLevelAccessOptions CompanyAndBranchOptions() => new()
    {
        DisableDefaultCountryFilter = true,
        DisableDefaultRegionFilter = true,
        DisableDefaultCityFilter = true,
        DisableDefaultBrandFilter = true,
        DisableDefaultTeamFilter = true,
    };

    private static DataLevelAccessPolicy<StandardScopedEntity> StandardPolicy(DefaultDataLevelAccessOptions options)
    {
        var builder = new DataLevelAccessBuilder<StandardScopedEntity>();
        builder.AddStandardDimensions(options);
        return new(builder);
    }

    private static DataLevelAccessContext DataContext(TypeAuthContext subject, ICurrentUserProvider claims)
        => new(new TypeAuthAccessibleItemsSource(subject), claims, new RecordingHashIdService());

    [Fact]
    public void RecordAccess_CanRequireGrantsFromSeveralSources()
    {
        var row = new StandardScopedEntity { CompanyID = 4, CompanyBranchID = 8 };
        var policy = StandardPolicy(CompanyAndBranchOptions());
        var claims = FakeCurrentUserProvider.Anonymous();

        // System access, company scope and branch scope may come from different direct/named trees.
        // Testing the whole record against each tree separately would incorrectly reject this subject.
        foreach (var tree in new[] { SystemGrant, CompanyGrant, BranchGrant })
        {
            var single = Subject(tree);
            Assert.False(single.Can(ShiftIdentityActions.Users, Access.Read)
                && policy.Authorize(row, Access.Read, DataContext(single, claims)));
        }

        var merged = Subject(SystemGrant, CompanyGrant, BranchGrant);
        Assert.True(merged.Can(ShiftIdentityActions.Users, Access.Read));
        Assert.True(policy.Authorize(row, Access.Read, DataContext(merged, claims)));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void SystemAndRecordAccess_AreBothRequired(bool systemGrant, bool dataGrant, bool expected)
    {
        var subject = Subject(systemGrant ? SystemGrant : "{}",
            dataGrant ? CompanyGrant : "{}", dataGrant ? BranchGrant : "{}");
        var policy = StandardPolicy(CompanyAndBranchOptions());
        var context = DataContext(subject, FakeCurrentUserProvider.Anonymous());
        var row = new StandardScopedEntity { CompanyID = 4, CompanyBranchID = 8 };

        Assert.Equal(expected, subject.Can(ShiftIdentityActions.Users, Access.Read)
            && policy.Authorize(row, Access.Read, context));
        Assert.False(subject.Can(ShiftIdentityActions.Users, Access.Write));
        Assert.False(policy.Authorize(row, Access.Write, context));
        Assert.False(policy.Authorize(row, Access.Delete, context));
    }

    [Fact]
    public void SharedSelfGrants_ResolveEachCandidatesOwnClaims()
    {
        var subject = Subject(SystemGrant, """
            {"ShiftIdentityActions":{"DataLevelAccess":{
                "Companies":{"@self@":["Read"]},
                "Teams":{"@self@":["Read"]}
            }}}
            """.Replace("@self@", TypeAuthContext.SelfReferenceKey));
        var options = CompanyAndBranchOptions();
        options.DisableDefaultCompanyBranchFilter = true;
        options.DisableDefaultTeamFilter = false;
        var policy = StandardPolicy(options);
        var row = new StandardScopedEntity { CompanyID = 4, TeamID = 12 };
        var first = DataContext(subject, FakeCurrentUserProvider.WithClaims(
            (Constants.CompanyIdClaim, "4"),
            (Constants.TeamIdsClaim, "11"), (Constants.TeamIdsClaim, "12")));
        var second = DataContext(subject, FakeCurrentUserProvider.WithClaims(
            (Constants.CompanyIdClaim, "5"), (Constants.TeamIdsClaim, "12")));
        var missing = DataContext(subject, FakeCurrentUserProvider.Anonymous());

        Assert.True(policy.Authorize(row, Access.Read, first));
        Assert.False(policy.Authorize(row, Access.Read, second));
        Assert.False(policy.Authorize(row, Access.Read, missing));
        Assert.True(policy.Authorize(row, Access.Read, first));
    }

    [Fact]
    public void LegacyDataAccess_CanUseAnExplicitCandidateWithoutHttpContext()
    {
        var subject = Subject(SystemGrant, CompanyGrant, BranchGrant);
        var claims = FakeCurrentUserProvider.Anonymous();
        var hashIds = new RecordingHashIdService();
        var legacy = new DefaultDataLevelAccess(subject, new IdentityClaimProvider(claims, hashIds), hashIds);
        var options = CompanyAndBranchOptions();
        var policy = StandardPolicy(options);
        var context = DataContext(subject, claims);
        var rows = new[]
        {
            new StandardScopedEntity { ID = 1, CompanyID = 4, CompanyBranchID = 8 },
            new StandardScopedEntity { ID = 2, CompanyID = 4, CompanyBranchID = 9 },
            new StandardScopedEntity { ID = 3, CompanyID = 5, CompanyBranchID = 8 },
        };

        Assert.True(subject.Can(ShiftIdentityActions.Users, Access.Read));
        Assert.Equal(new long[] { 1 }, legacy.ApplyDefaultDataLevelFilters(options, rows.AsQueryable())
            .Select(row => row.ID).ToArray());
        foreach (var row in rows)
            foreach (var operation in new[] { Access.Read, Access.Write, Access.Delete })
                Assert.Equal(policy.Authorize(row, operation, context),
                    legacy.HasDefaultDataLevelAccess(options, row, operation));
    }
}
