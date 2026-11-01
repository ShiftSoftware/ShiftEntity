using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newtonsoft.Json;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.ShiftEntity.Web.Services;
using ShiftSoftware.ShiftIdentity.Core;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Company;
using ShiftSoftware.TypeAuth.Core;
using Xunit;
using Claims = ShiftSoftware.ShiftEntity.Core.Constants;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// A repository answers for another user. For read, write and delete, <c>CanAccessAs</c> gives the answer that the
/// repository's own find, update and delete give that user when that user makes the request. The repository that
/// answers is always built for a different request user, whose own claims point elsewhere, so an answer taken from
/// the request would show up as a difference.
/// </summary>
public class CanAccessAsTests
{
    public sealed class RowsDbContext(DbContextOptions<RowsDbContext> options) : ShiftDbContext(options)
    {
        public DbSet<StandardScopedEntity> Rows => Set<StandardScopedEntity>();
    }

    public sealed class RowDTO : ShiftEntityDTOBase
    {
        public override string? ID { get; set; }
        public string Name { get; set; } = "";
        public long? CountryID { get; set; }
        public long? RegionID { get; set; }
        public long? CompanyID { get; set; }
        public long? CompanyBranchID { get; set; }
        public long? BrandID { get; set; }
        public long? CityID { get; set; }
        public long? TeamID { get; set; }
    }

    // An update writes the row's own values back, so the Write check sees the row as it is stored.
    private sealed class RowMapper : IShiftEntityMapper<StandardScopedEntity, RowDTO, RowDTO>
    {
        public StandardScopedEntity MapToEntity(RowDTO dto, StandardScopedEntity existing, MappingContext context = default)
        {
            existing.Name = dto.Name;
            existing.CountryID = dto.CountryID;
            existing.RegionID = dto.RegionID;
            existing.CompanyID = dto.CompanyID;
            existing.CompanyBranchID = dto.CompanyBranchID;
            existing.BrandID = dto.BrandID;
            existing.CityID = dto.CityID;
            existing.TeamID = dto.TeamID;
            return existing;
        }

        public RowDTO MapToView(StandardScopedEntity entity, MappingContext context = default) => throw new NotSupportedException();
        public IQueryable<RowDTO> MapToList(IQueryable<StandardScopedEntity> query, MappingContext context = default) => throw new NotSupportedException();
        public void CopyEntity(StandardScopedEntity source, StandardScopedEntity target, MappingContext context = default) => throw new NotSupportedException();
    }

    /// <summary>One user: grants and claims.</summary>
    public sealed class TestUser(string name, string tree, params (string Type, string Value)[] claims)
    {
        public string Name { get; } = name;
        public ITypeAuthService TypeAuth() => new TypeAuthContextBuilder().AddAccessTree(tree).AddActionTree<ShiftIdentityActions>().Build();
        public ICurrentUserProvider Claims() => FakeCurrentUserProvider.WithClaims(claims);
        public override string ToString() => Name;
    }

    // The user of the request a scope serves. Set right after the scope is created.
    private sealed class RequestUser
    {
        public TestUser User { get; set; } = null!;
    }

    private static readonly string[] Dimensions = ["Countries", "Regions", "Companies", "Branches", "Brands", "Cities", "Teams"];
    private static readonly Access[] Levels = [Access.Read, Access.Write, Access.Delete];

    // Every data-level dimension open at every level (a wildcard), except the dimensions given.
    private static string Tree(params (string Dimension, object Grant)[] grants)
    {
        var dataLevel = Dimensions.ToDictionary(dimension => dimension, _ => (object)Levels);
        foreach (var (dimension, grant) in grants)
            dataLevel[dimension] = grant;

        return JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            [nameof(ShiftIdentityActions)] = new Dictionary<string, object> { [nameof(ShiftIdentityActions.DataLevelAccess)] = dataLevel },
        });
    }

    private static Dictionary<string, Access[]> Keys(Access[] levels, params string[] keys)
        => keys.ToDictionary(key => key, _ => levels);

    private static readonly TestUser Wide = new("wide", Tree());
    private static readonly TestUser CompanyFourReadOnly = new("company 4, read only", Tree(("Companies", Keys([Access.Read], "4"))));
    private static readonly TestUser CompanyFive = new("company 5", Tree(("Companies", Keys(Levels, "5"))));
    private static readonly TestUser OwnCompany = new("own company, read and write",
        Tree(("Companies", Keys([Access.Read, Access.Write], TypeAuthContext.SelfReferenceKey))), (Claims.CompanyIdClaim, "4"));
    private static readonly TestUser OwnTeams = new("own teams",
        Tree(("Teams", Keys(Levels, TypeAuthContext.SelfReferenceKey))), (Claims.TeamIdsClaim, "11"), (Claims.TeamIdsClaim, "13"));
    private static readonly TestUser NullCompany = new("null company", Tree(("Companies", Keys(Levels, TypeAuthContext.EmptyOrNullKey))));
    private static readonly TestUser Nobody = new("nobody", "{}");

    // The user of the request that builds the answering repository. Its own claims point at company 5 and team 12.
    private static readonly TestUser Requester = new("requester",
        Tree(("Companies", Keys(Levels, TypeAuthContext.SelfReferenceKey)), ("Teams", Keys(Levels, TypeAuthContext.SelfReferenceKey))),
        (Claims.CompanyIdClaim, "5"), (Claims.TeamIdsClaim, "12"));

    private static readonly TestUser[] Candidates = [Wide, CompanyFourReadOnly, CompanyFive, OwnCompany, OwnTeams, NullCompany, Nobody, Requester];

    private static StandardScopedEntity Row(long id, long? company, long? team) => new()
    {
        ID = id, Name = "row " + id, CountryID = 1, RegionID = 2, CompanyID = company, CompanyBranchID = 3, BrandID = 5, CityID = 6, TeamID = team,
    };

    private static List<StandardScopedEntity> Rows() => [Row(1, 4, 11), Row(2, 5, 12), Row(3, 4, 13), Row(4, null, null)];

    private static RowDTO Dto(StandardScopedEntity row) => new()
    {
        ID = row.ID.ToString(), Name = row.Name, CountryID = row.CountryID, RegionID = row.RegionID, CompanyID = row.CompanyID,
        CompanyBranchID = row.CompanyBranchID, BrandID = row.BrandID, CityID = row.CityID, TeamID = row.TeamID,
    };

    private static async Task<ServiceProvider> HostAsync(bool profile, bool factory = true)
    {
        var database = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<RowsDbContext>(options => options.UseInMemoryDatabase(database));
        services.AddScoped<RequestUser>();
        services.AddScoped<ITypeAuthService>(sp => sp.GetRequiredService<RequestUser>().User.TypeAuth());
        services.AddScoped<ICurrentUserProvider>(sp => sp.GetRequiredService<RequestUser>().User.Claims());
        services.AddScoped<IdentityClaimProvider>();
        services.AddSingleton<IHashIdService>(new RecordingHashIdService());
        // The real legacy evaluator, as the web registration adds it, and its factory for other users.
        services.AddScoped<IDefaultDataLevelAccess, DefaultDataLevelAccess>();
        if (factory)
            services.AddScoped<IDefaultDataLevelAccessFactory, DefaultDataLevelAccessFactory>();

        if (profile)
            services.AddShiftEntityDataLevelAccess();
        else
        {
            services.TryAddScoped<IAccessibleItemsSource, TypeAuthAccessibleItemsSource>();
            services.TryAddScoped<DataLevelAccessContext>();
        }

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestUser>().User = Wide;
        var db = scope.ServiceProvider.GetRequiredService<RowsDbContext>();
        db.Rows.AddRange(Rows());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return provider;
    }

    private static ShiftRepository<RowsDbContext, StandardScopedEntity, RowDTO, RowDTO> Repository(
        IServiceScope scope, TestUser user, Action<ShiftRepositoryOptions<StandardScopedEntity, RowDTO, RowDTO>>? configure)
    {
        scope.ServiceProvider.GetRequiredService<RequestUser>().User = user;
        return new(scope.ServiceProvider.GetRequiredService<RowsDbContext>(), options =>
        {
            options.UseMapper(new RowMapper());
            configure?.Invoke(options);
        });
    }

    // What the repository's own find, update and delete give the user, when the user makes the request.
    private static async Task<bool> OwnAnswerAsync(ServiceProvider provider, Scenario scenario, TestUser user, long rowId, Access access)
    {
        await using var scope = provider.CreateAsyncScope();
        var repository = Repository(scope, user, scenario.Configure);
        var row = Rows().Single(x => x.ID == rowId);

        try
        {
            switch (access)
            {
                case Access.Read:
                    return await repository.FindAsync(rowId, asOf: null, disableDefaultDataLevelAccess: false, disableGlobalFilters: false) is not null;
                case Access.Write:
                    await repository.UpsertAsync(row, Dto(row), ActionTypes.Update, userId: null, idempotencyKey: null,
                        disableDefaultDataLevelAccess: false, disableGlobalFilters: false);
                    return true;
                default:
                    await repository.DeleteAsync(row, userId: null, disableDefaultDataLevelAccess: false, disableGlobalFilters: false);
                    return true;
            }
        }
        catch (ShiftEntityException exception) when (exception.HttpStatusCode == (int)HttpStatusCode.Forbidden)
        {
            return false;
        }
    }

    public sealed record Scenario(string Name, bool Profile, Action<ShiftRepositoryOptions<StandardScopedEntity, RowDTO, RowDTO>>? Configure);

    // Checks every candidate, row and level, and returns the answers so a test can also pin some of them.
    private static async Task<Dictionary<(TestUser User, long Row, Access Access), bool>> AssertSameAnswersAsync(Scenario scenario)
    {
        await using var provider = await HostAsync(scenario.Profile);
        await using var scope = provider.CreateAsyncScope();
        var answering = Repository(scope, Requester, scenario.Configure);
        var answers = new Dictionary<(TestUser, long, Access), bool>();

        foreach (var user in Candidates)
            foreach (var row in Rows())
                foreach (var access in Levels)
                {
                    var own = await OwnAnswerAsync(provider, scenario, user, row.ID, access);
                    var asked = answering.CanAccessAs(row, access, user.TypeAuth(), user.Claims());

                    Assert.True(own == asked,
                        $"{scenario.Name}: {user} on row {row.ID} at {access}. Own answer {own}, CanAccessAs {asked}.");

                    answers[(user, row.ID, access)] = own;
                }

        return answers;
    }

    private static void AssertAll(Dictionary<(TestUser User, long Row, Access Access), bool> answers, TestUser user, bool expected)
        => Assert.All(answers.Where(answer => answer.Key.User == user), answer => Assert.Equal(expected, answer.Value));

    [Fact]
    public async Task On_the_standard_profile_CanAccessAs_gives_each_users_own_answer()
    {
        var answers = await AssertSameAnswersAsync(new("standard profile", Profile: true, Configure: null));

        AssertAll(answers, Wide, true);
        AssertAll(answers, Nobody, false);
        Assert.True(answers[(CompanyFourReadOnly, 1, Access.Read)]);
        Assert.False(answers[(CompanyFourReadOnly, 1, Access.Write)]);
        Assert.False(answers[(CompanyFourReadOnly, 2, Access.Read)]);
        Assert.False(answers[(CompanyFive, 1, Access.Read)]);
        Assert.True(answers[(OwnCompany, 3, Access.Write)]);
        Assert.False(answers[(OwnCompany, 3, Access.Delete)]);
        Assert.True(answers[(OwnTeams, 3, Access.Delete)]);
        Assert.False(answers[(OwnTeams, 2, Access.Read)]);
        Assert.True(answers[(NullCompany, 4, Access.Read)]);
        Assert.True(answers[(Requester, 2, Access.Read)]);
        Assert.False(answers[(Requester, 1, Access.Read)]);
    }

    [Fact]
    public async Task On_a_legacy_repository_with_some_default_filters_off_CanAccessAs_gives_each_users_own_answer()
    {
        // A legacy repository that turns off its team, city and country filters, on a host without the v2 profile.
        var answers = await AssertSameAnswersAsync(new("legacy, team city and country filters off", Profile: false, Configure: options =>
            options.DefaultDataLevelAccessOptions = new DefaultDataLevelAccessOptions
            {
                DisableDefaultTeamFilter = true,
                DisableDefaultCityFilter = true,
                DisableDefaultCountryFilter = true,
            }));

        AssertAll(answers, Wide, true);
        AssertAll(answers, Nobody, false);
        AssertAll(answers, OwnTeams, true);
        Assert.True(answers[(OwnCompany, 1, Access.Write)]);
        Assert.False(answers[(OwnCompany, 2, Access.Read)]);
        Assert.True(answers[(Requester, 2, Access.Delete)]);
        Assert.False(answers[(Requester, 3, Access.Read)]);
    }

    [Fact]
    public async Task With_an_explicit_declaration_over_the_profile_CanAccessAs_gives_each_users_own_answer()
    {
        // The declaration replaces the profile's company dimension: a row is in reach through its company or its brand.
        var answers = await AssertSameAnswersAsync(new("explicit company dimension over the profile", Profile: true, Configure: options =>
            options.DataLevelAccess(access => access.On(ShiftIdentityActions.DataLevelAccess.Companies)
                .Match(ids => row => ids.Contains(row.CompanyID) || ids.Contains(row.BrandID))
                .HashId<CompanyDTO>()
                .Self(Claims.CompanyIdClaim))));

        AssertAll(answers, Wide, true);
        AssertAll(answers, Nobody, false);
        // Every row has brand 5, so company 5 reaches all of them here, and only row 2 on the profile alone.
        AssertAll(answers, CompanyFive, true);
        Assert.False(answers[(CompanyFourReadOnly, 2, Access.Read)]);
    }

    [Fact]
    public async Task On_an_unscoped_repository_CanAccessAs_lets_every_user_through_like_the_repository()
    {
        var answers = await AssertSameAnswersAsync(new("unscoped", Profile: true, Configure: options =>
            options.DataLevelAccess(access => access.Unscoped())));

        Assert.All(answers.Values, answer => Assert.True(answer));
    }

    [Fact]
    public async Task Self_and_SelfMany_read_the_given_users_claims_and_not_the_requesters()
    {
        await using var provider = await HostAsync(profile: true);
        await using var scope = provider.CreateAsyncScope();
        var asRequester = Repository(scope, Requester, configure: null);
        var companyFourTeamEleven = Row(1, 4, 11);
        var companyFiveTeamTwelve = Row(2, 5, 12);

        // The requester's own claims are company 5 and team 12.
        Assert.False(asRequester.CanAccessAs(companyFourTeamEleven, Access.Read, Requester.TypeAuth(), Requester.Claims()));
        Assert.True(asRequester.CanAccessAs(companyFiveTeamTwelve, Access.Read, Requester.TypeAuth(), Requester.Claims()));

        // Self: the given user's company claim is 4.
        Assert.True(asRequester.CanAccessAs(companyFourTeamEleven, Access.Read, OwnCompany.TypeAuth(), OwnCompany.Claims()));
        Assert.False(asRequester.CanAccessAs(companyFiveTeamTwelve, Access.Read, OwnCompany.TypeAuth(), OwnCompany.Claims()));

        // SelfMany: the given user's team claims are 11 and 13.
        Assert.True(asRequester.CanAccessAs(companyFourTeamEleven, Access.Read, OwnTeams.TypeAuth(), OwnTeams.Claims()));
        Assert.True(asRequester.CanAccessAs(Row(3, 4, 13), Access.Read, OwnTeams.TypeAuth(), OwnTeams.Claims()));
        Assert.False(asRequester.CanAccessAs(companyFiveTeamTwelve, Access.Read, OwnTeams.TypeAuth(), OwnTeams.Claims()));
    }

    [Fact]
    public async Task An_enabled_global_filter_refuses_and_a_disabled_one_does_not()
    {
        await using var provider = await HostAsync(profile: true);

        await using (var scope = provider.CreateAsyncScope())
        {
            var filtered = Repository(scope, Requester, options =>
                options.FilterByClaimValues(context => context.ClaimValues!.Contains(context.Entity.Name)));

            var refusal = Assert.Throws<InvalidOperationException>(() =>
                filtered.CanAccessAs(Row(1, 4, 11), Access.Read, Wide.TypeAuth(), Wide.Claims()));
            Assert.Contains("global filter", refusal.Message);
            Assert.Contains("DataLevelAccess", refusal.Message);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var turnedOff = Repository(scope, Requester, options =>
                options.FilterByClaimValues(context => context.ClaimValues!.Contains(context.Entity.Name), disabled: true));

            Assert.True(turnedOff.CanAccessAs(Row(1, 4, 11), Access.Read, Wide.TypeAuth(), Wide.Claims()));
        }
    }

    [Fact]
    public async Task The_legacy_path_refuses_when_no_factory_is_registered()
    {
        await using var provider = await HostAsync(profile: false, factory: false);
        await using var scope = provider.CreateAsyncScope();

        var legacy = Repository(scope, Requester, configure: null);
        var refusal = Assert.Throws<InvalidOperationException>(() =>
            legacy.CanAccessAs(Row(1, 4, 11), Access.Read, Wide.TypeAuth(), Wide.Claims()));
        Assert.Contains(nameof(IDefaultDataLevelAccessFactory), refusal.Message);

        // A v2 declaration needs no factory.
        var declared = Repository(scope, Requester, options =>
            options.DataLevelAccess(access => access.On(ShiftIdentityActions.DataLevelAccess.Companies)
                .Key(row => row.CompanyID).HashId<CompanyDTO>()));
        Assert.False(declared.CanAccessAs(Row(1, 4, 11), Access.Read, CompanyFive.TypeAuth(), CompanyFive.Claims()));
        Assert.True(declared.CanAccessAs(Row(2, 5, 12), Access.Read, CompanyFive.TypeAuth(), CompanyFive.Claims()));
    }

    [Fact]
    public void A_repository_that_cannot_answer_for_another_user_refuses()
    {
        IShiftRepositoryAsync<StandardScopedEntity, RowDTO, RowDTO> repository = new NotAnswering();

        Assert.Throws<NotSupportedException>(() =>
            repository.CanAccessAs(Row(1, 4, 11), Access.Read, Wide.TypeAuth(), Wide.Claims()));
    }

    // A repository that implements the interface but not CanAccessAs, so the interface's default answers.
    private sealed class NotAnswering : IShiftRepositoryAsync<StandardScopedEntity, RowDTO, RowDTO>
    {
        public Message? ResponseMessage { get; set; }
        public Dictionary<string, object>? AdditionalResponseData { get; set; }
        public void Add(StandardScopedEntity entity) => throw new NotSupportedException();
        public Task<int> SaveChangesAsync() => throw new NotSupportedException();
        public ValueTask<StandardScopedEntity> UpsertAsync(StandardScopedEntity entity, RowDTO dto, ActionTypes actionType, long? userId,
            Guid? idempotencyKey, bool disableDefaultDataLevelAccess, bool disableGlobalFilters) => throw new NotSupportedException();
        public ValueTask<IQueryable<RowDTO>> OdataList(IQueryable<StandardScopedEntity>? queryable) => throw new NotSupportedException();
        public ValueTask<IQueryable<StandardScopedEntity>> GetIQueryable(DateTimeOffset? asOf, List<string>? includes,
            bool disableDefaultDataLevelAccess, bool disableGlobalFilters) => throw new NotSupportedException();
        public ValueTask<IQueryable<RowDTO>> ApplyPostODataProcessing(IQueryable<RowDTO> queryable) => throw new NotSupportedException();
        public Task<StandardScopedEntity?> FindAsync(long id, DateTimeOffset? asOf, bool disableDefaultDataLevelAccess,
            bool disableGlobalFilters) => throw new NotSupportedException();
        public Task<StandardScopedEntity?> FindByIdempotencyKeyAsync(Guid idempotencyKey, DateTimeOffset? asOf,
            bool disableDefaultDataLevelAccess, bool disableGlobalFilters) => throw new NotSupportedException();
        public IQueryable<RevisionDTO> GetRevisionsAsync(long id) => throw new NotSupportedException();
        public Task<Stream> PrintAsync(string id) => throw new NotSupportedException();
        public ValueTask<RowDTO> ViewAsync(StandardScopedEntity entity) => throw new NotSupportedException();
        public ValueTask<StandardScopedEntity> DeleteAsync(StandardScopedEntity entity, long? userId,
            bool disableDefaultDataLevelAccess, bool disableGlobalFilters) => throw new NotSupportedException();
    }
}
