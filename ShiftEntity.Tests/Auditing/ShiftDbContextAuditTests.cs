using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.Auditing.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Auditing;

/// <summary>
/// The audit backfill also runs in <c>ShiftDbContext.SaveChanges</c>/<c>SaveChangesAsync</c> — the fallback for rows
/// saved <em>directly</em> through the context (no repository, no <c>repository.SaveChangesAsync()</c>). It stamps the
/// same columns with the same rules (set only where unset; create stamps + org claims are insert-only; last-save
/// always advances) and skips rows a repository already stamped via the <c>AuditFieldsAreSet</c> guard.
///
/// <para>Crucially, the context resolves the acting user defensively: if the claim services aren't registered (a bare
/// context, a migration, a DI-less unit test) or a value is absent, the columns are left null/default — the save must
/// not throw.</para>
/// </summary>
public class ShiftDbContextAuditTests
{
    private const long ActorUser = 8, ActorCompany = 100, ActorBranch = 200, ActorCountry = 300, ActorRegion = 400, ActorCity = 500;

    private static FakeUserProvider Actor() => FakeUserProvider.WithClaims(
        userId: ActorUser, companyId: ActorCompany, branchId: ActorBranch,
        countryId: ActorCountry, regionId: ActorRegion, cityId: ActorCity);

    private static void AssertActorOrgStamps(IHasOrgClaims e)
    {
        Assert.Equal(ActorCompany, e.CompanyID);
        Assert.Equal(ActorBranch, e.CompanyBranchID);
        Assert.Equal(ActorCountry, e.CountryID);
        Assert.Equal(ActorRegion, e.RegionID);
        Assert.Equal(ActorCity, e.CityID);
    }

    private static void AssertNoOrgStamps(IHasOrgClaims e)
    {
        Assert.Null(e.CompanyID);
        Assert.Null(e.CompanyBranchID);
        Assert.Null(e.CountryID);
        Assert.Null(e.RegionID);
        Assert.Null(e.CityID);
    }

    // ── Direct context save (DI present) ──────────────────────────────────────

    [Fact]
    public async Task DirectSave_StampsAllAuditColumns_FromCurrentUser()
    {
        using var provider = AuditingHost.Build(Actor);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var order = new OrderEntity { Number = "A-1" };
        db.Orders.Add(order);                 // straight to the context — no repository

        var before = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        Assert.True(order.CreateDate >= before);
        Assert.True(order.LastSaveDate >= before);
        Assert.False(order.IsDeleted);
        Assert.True(order.AuditFieldsAreSet);
        Assert.Equal(ActorUser, order.CreatedByUserID);
        Assert.Equal(ActorUser, order.LastSavedByUserID);
        AssertActorOrgStamps(order);
    }

    [Fact]
    public async Task RepositorySave_SuppressesContextSweep_ButResetsIt_SoLaterDirectSaveStillStamps()
    {
        // A repository save stamps the rows itself and suppresses the context's (identical) sweep for that save. The
        // suppression must be reset afterwards: a later DIRECT save on the SAME context must still be stamped. (If the
        // flag leaked, `direct` would come back unstamped — null user/org — and this would fail.)
        using var provider = AuditingHost.Build(Actor);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = new ShiftRepository<OrderingDbContext, OrderEntity, OrderListDTO, OrderListDTO>(
            db, new ThrowingMapper<OrderEntity, OrderListDTO>());

        var viaRepo = new OrderEntity { Number = "via-repo" };
        repo.Add(viaRepo);
        await repo.SaveChangesAsync();           // repository-initiated: context sweep suppressed, then reset
        Assert.Equal(ActorUser, viaRepo.CreatedByUserID);
        AssertActorOrgStamps(viaRepo);

        var direct = new OrderEntity { Number = "direct" };
        db.Orders.Add(direct);
        await db.SaveChangesAsync();             // direct on the same context: must still be stamped
        Assert.Equal(ActorUser, direct.CreatedByUserID);
        AssertActorOrgStamps(direct);
    }

    [Fact]
    public async Task DirectSave_StampsChildrenAndUnrelatedRows()
    {
        using var provider = AuditingHost.Build(Actor);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var order = new OrderEntity
        {
            Number = "A-1",
            Lines = { new OrderLineEntity { Sku = "SKU-1", Quantity = 2 } },
        };
        var category = new CategoryEntity { Name = "Unrelated" };
        db.Orders.Add(order);
        db.Categories.Add(category);

        await db.SaveChangesAsync();

        AssertActorOrgStamps(order);
        Assert.Equal(ActorUser, order.CreatedByUserID);
        Assert.All(order.Lines, line =>
        {
            Assert.Equal(ActorUser, line.CreatedByUserID);
            AssertActorOrgStamps(line);
        });
        Assert.Equal(ActorUser, category.CreatedByUserID);
        AssertActorOrgStamps(category);
    }

    [Fact]
    public async Task DirectSave_Update_AdvancesLastSave_PreservesCreateAndOrg()
    {
        using var provider = AuditingHost.Build(Actor);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var order = new OrderEntity { Number = "A-1" };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var createdAt = order.CreateDate;
        order.AuditFieldsAreSet = false; // mimic a reload (the [NotMapped] guard is not persisted)
        order.Number = "A-1-edited";
        var beforeUpdate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        Assert.Equal(createdAt, order.CreateDate);       // create stamp untouched
        Assert.True(order.LastSaveDate >= beforeUpdate); // last-save advanced
        AssertActorOrgStamps(order);                     // org provenance unchanged
    }

    [Fact]
    public async Task DirectSave_PreservesManuallySetFields()
    {
        using var provider = AuditingHost.Build(Actor);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var manualCreate = new DateTimeOffset(2021, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var order = new OrderEntity { Number = "A-1", CreateDate = manualCreate, CreatedByUserID = 999, CompanyID = 777 };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        Assert.Equal(manualCreate, order.CreateDate);     // manual values kept
        Assert.Equal(999, order.CreatedByUserID);
        Assert.Equal(777, order.CompanyID);
        Assert.Equal(ActorUser, order.LastSavedByUserID); // unset ones filled
        Assert.Equal(ActorBranch, order.CompanyBranchID);
    }

    [Fact]
    public async Task DirectSave_RespectsGuard_SkipsAlreadyStampedRow()
    {
        using var provider = AuditingHost.Build(Actor);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var order = new OrderEntity { Number = "A-1", AuditFieldsAreSet = true }; // pretend a repository handled it
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        Assert.Equal(default, order.CreateDate);
        Assert.Null(order.CreatedByUserID);
        AssertNoOrgStamps(order);
    }

    [Fact]
    public async Task DirectSave_Anonymous_StampsDates_LeavesUserAndOrgNull()
    {
        using var provider = AuditingHost.Build(); // anonymous
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var order = new OrderEntity { Number = "A-1" };
        db.Orders.Add(order);
        var before = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        Assert.True(order.CreateDate >= before);
        Assert.Null(order.CreatedByUserID);
        AssertNoOrgStamps(order);
    }

    [Fact]
    public async Task DirectSave_AuthenticatedUserMissingOrgClaims_LeavesThemNull_NotZero()
    {
        // Service registered, user authenticated, but no org claims present: absent claims resolve to null, not 0.
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(42));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var order = new OrderEntity { Number = "A-1" };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        Assert.Equal(42, order.CreatedByUserID);
        AssertNoOrgStamps(order);
    }

    // ── Defensive resolution: no claim services registered ────────────────────

    [Fact]
    public async Task DirectSave_WithoutClaimServices_DoesNotThrow_AndStampsDatesWithNullUser()
    {
        // A bare context with no application DI (no IdentityClaimProvider / IHashIdService). Resolving the acting
        // user must fall back to null rather than throw, and the save must still succeed and stamp the dates.
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new OrderingDbContext(options);

        var order = new OrderEntity { Number = "A-1" };
        db.Orders.Add(order);

        var before = DateTimeOffset.UtcNow;
        var saved = await db.SaveChangesAsync(); // must not throw

        Assert.True(saved >= 1);
        Assert.True(order.CreateDate >= before);
        Assert.True(order.LastSaveDate >= before);
        Assert.True(order.AuditFieldsAreSet);
        Assert.Null(order.CreatedByUserID);
        Assert.Null(order.LastSavedByUserID);
        AssertNoOrgStamps(order);
    }

    [Fact]
    public void DirectSave_Synchronous_AlsoStampsAndDoesNotThrow_WithoutClaimServices()
    {
        // The synchronous SaveChanges override gets the same backfill and the same defensive resolution.
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new OrderingDbContext(options);

        var order = new OrderEntity { Number = "A-1" };
        db.Orders.Add(order);

        var before = DateTimeOffset.UtcNow;
        db.SaveChanges(); // must not throw

        Assert.True(order.CreateDate >= before);
        Assert.True(order.AuditFieldsAreSet);
        Assert.Null(order.CreatedByUserID);
    }
}
