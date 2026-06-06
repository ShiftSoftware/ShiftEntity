using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.Auditing.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Auditing;

/// <summary>
/// The SaveChanges sweep backfills the org/location claim columns (CompanyID, CompanyBranchID, CountryID, RegionID,
/// CityID) the same way it stamps the user/date columns: from the acting user's claims, for every changed auditable
/// row that upsert didn't already handle. So rows that never go through <c>UpsertAsync</c> — cascaded children and
/// unrelated entities saved with a plain <c>Add</c> + <c>SaveChanges</c> — still get them.
///
/// <para>These claim columns are <b>insert-only</b> provenance: filled on insert only where left unset (a manual
/// value wins), and never re-derived on update. A row already stamped upstream (the <c>AuditFieldsAreSet</c> guard is
/// set by <c>UpsertAsync</c>) is skipped entirely.</para>
/// </summary>
public class ClaimBackfillTests
{
    private static ShiftRepository<OrderingDbContext, OrderEntity, OrderListDTO, OrderListDTO> OrderRepo(OrderingDbContext db)
        => new(db, new ThrowingMapper<OrderEntity, OrderListDTO>());

    private static ShiftRepository<OrderingDbContext, CategoryEntity, CategoryListDTO, CategoryListDTO> CategoryRepo(OrderingDbContext db)
        => new(db, new ThrowingMapper<CategoryEntity, CategoryListDTO>());

    private const long ActorCompany = 100, ActorBranch = 200, ActorCountry = 300, ActorRegion = 400, ActorCity = 500;

    private static FakeUserProvider Actor() => FakeUserProvider.WithClaims(
        userId: 8, companyId: ActorCompany, branchId: ActorBranch,
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

    // ── Backfill on insert ────────────────────────────────────────────────────

    [Fact]
    public async Task Insert_SweepBackfillsOrgClaims_OnRoot()
    {
        using var provider = AuditingHost.Build(Actor);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var order = new OrderEntity { Number = "A-1" };
        repo.Add(order);
        await repo.SaveChangesAsync();

        AssertActorOrgStamps(order);
    }

    [Fact]
    public async Task Insert_SweepBackfillsOrgClaims_OnChildrenOfDifferentType()
    {
        using var provider = AuditingHost.Build(Actor);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var order = new OrderEntity
        {
            Number = "A-1",
            Lines =
            {
                new OrderLineEntity { Sku = "SKU-1", Quantity = 2 },
                new OrderLineEntity { Sku = "SKU-2", Quantity = 5 },
            },
        };
        repo.Add(order);
        await repo.SaveChangesAsync();

        AssertActorOrgStamps(order);
        Assert.All(order.Lines, line => AssertActorOrgStamps(line));
    }

    [Fact]
    public async Task Insert_SweepBackfillsOrgClaims_OnSameTypeChildren()
    {
        using var provider = AuditingHost.Build(Actor);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = CategoryRepo(db);

        var root = new CategoryEntity
        {
            Name = "Root",
            Children = { new CategoryEntity { Name = "Child A" }, new CategoryEntity { Name = "Child B" } },
        };
        repo.Add(root);
        await repo.SaveChangesAsync();

        AssertActorOrgStamps(root);
        Assert.All(root.Children, child => AssertActorOrgStamps(child));
    }

    [Fact]
    public async Task SaveChanges_BackfillsOrgClaims_OnUnrelatedEntity()
    {
        // Two unrelated aggregates in one unit of work, flushed once through the Order repository — both get the
        // org claims, regardless of which repository did the flush.
        using var provider = AuditingHost.Build(Actor);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var orderRepo = OrderRepo(db);
        var categoryRepo = CategoryRepo(db);

        var order = new OrderEntity { Number = "A-1" };
        var category = new CategoryEntity { Name = "Unrelated" };
        orderRepo.Add(order);
        categoryRepo.Add(category);
        await orderRepo.SaveChangesAsync();

        AssertActorOrgStamps(order);
        AssertActorOrgStamps(category);
    }

    // ── Only-if-null and insert-only semantics ────────────────────────────────

    [Fact]
    public async Task Insert_PreservesManuallySetOrgClaim_AndFillsTheRest()
    {
        using var provider = AuditingHost.Build(Actor);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var order = new OrderEntity { Number = "A-1", CompanyID = 777 }; // explicit company, rest unset
        repo.Add(order);
        await repo.SaveChangesAsync();

        Assert.Equal(777, order.CompanyID);                 // manual value kept
        Assert.Equal(ActorBranch, order.CompanyBranchID);   // unset ones backfilled
        Assert.Equal(ActorCountry, order.CountryID);
        Assert.Equal(ActorRegion, order.RegionID);
        Assert.Equal(ActorCity, order.CityID);
    }

    [Fact]
    public async Task Update_DoesNotBackfillOrgClaims_InsertOnly()
    {
        using var provider = AuditingHost.Build(Actor);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var order = new OrderEntity { Number = "A-1" };
        repo.Add(order);
        await repo.SaveChangesAsync();
        AssertActorOrgStamps(order); // backfilled on insert

        order.AuditFieldsAreSet = false; // fresh unit-of-work
        order.CompanyID = null;          // clear a provenance column, then update
        order.Number = "A-1-edited";
        await repo.SaveChangesAsync();

        Assert.Null(order.CompanyID); // update never re-derives provenance
    }

    [Fact]
    public async Task Insert_Anonymous_LeavesOrgClaimsNull()
    {
        using var provider = AuditingHost.Build(); // anonymous by default
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var order = new OrderEntity { Number = "A-1" };
        repo.Add(order);
        await repo.SaveChangesAsync();

        AssertNoOrgStamps(order); // no claims ⇒ nothing to backfill
    }
}
