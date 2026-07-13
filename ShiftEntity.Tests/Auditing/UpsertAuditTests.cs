using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Tests.Auditing.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Auditing;

/// <summary>
/// Audit behavior of the row-mutating repository entry points, <c>UpsertAsync</c> and <c>DeleteAsync</c> — the CRUD
/// path the web handlers call. These stamp the "who" columns directly on the operation (the SaveChanges sweep, covered
/// by <see cref="AuditFieldsTests"/>, then leaves the already-stamped row alone via the <c>AuditFieldsAreSet</c> guard).
///
/// <para>The acting user is sourced from the same <c>IdentityClaimProvider</c> the org/location claims come from: when
/// the caller doesn't pass a <c>userId</c>, it falls back to the current user's claim. So <c>CreatedByUserID</c> (on
/// insert), <c>LastSavedByUserID</c> (insert/update, and the deleter on delete) are populated from context "like the
/// others", while an explicitly-passed id — or a value already on the entity — wins. There is no dedicated
/// <c>DeletedByUserID</c> column, so the deleter is recorded in <c>LastSavedByUserID</c>.</para>
/// </summary>
public class UpsertAuditTests
{
    /// <summary>Real mapper — <c>UpsertAsync</c> maps the DTO onto the entity, so this can't be the throwing double.</summary>
    private sealed class OrderUpsertMapper : IShiftEntityMapper<OrderEntity, OrderListDTO, OrderListDTO>
    {
        public OrderEntity MapToEntity(OrderListDTO dto, OrderEntity existing, MappingContext context = default)
        {
            existing.Number = dto.Number;
            return existing;
        }

        public OrderListDTO MapToView(OrderEntity entity, MappingContext context = default) => throw new NotSupportedException();
        public IQueryable<OrderListDTO> MapToList(IQueryable<OrderEntity> query, MappingContext context = default) => throw new NotSupportedException();
        public void CopyEntity(OrderEntity source, OrderEntity target, MappingContext context = default) => throw new NotSupportedException();
    }

    private static ShiftRepository<OrderingDbContext, OrderEntity, OrderListDTO, OrderListDTO> Repo(OrderingDbContext db)
        => new(db, o => o.UseMapper(new OrderUpsertMapper()));

    private static OrderingDbContext Db(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

    // ── Upsert: insert ────────────────────────────────────────────────────────

    [Fact]
    public async Task Upsert_Insert_StampsCreatedAndLastSavedBy_FromCurrentUser_WhenUserIdNotPassed()
    {
        // No userId passed ⇒ both "who" columns come from the signed-in user's claim, exactly like the org claims.
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(50));
        using var scope = provider.CreateScope();
        var repo = Repo(Db(scope));

        var entity = await repo.UpsertAsync(
            new OrderEntity(), new OrderListDTO { Number = "A-1" }, ActionTypes.Insert,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal(50, entity.CreatedByUserID);
        Assert.Equal(50, entity.LastSavedByUserID);
    }

    [Fact]
    public async Task Upsert_Insert_ExplicitUserIdWins_OverCurrentUser()
    {
        // An explicitly-passed userId takes precedence over the current user's claim.
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(50));
        using var scope = provider.CreateScope();
        var repo = Repo(Db(scope));

        var entity = await repo.UpsertAsync(
            new OrderEntity(), new OrderListDTO { Number = "A-1" }, ActionTypes.Insert,
            userId: 7, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal(7, entity.CreatedByUserID);
        Assert.Equal(7, entity.LastSavedByUserID);
    }

    [Fact]
    public async Task Upsert_Insert_PreservesManuallySetCreatedByUserID()
    {
        // A value already on the entity wins over both the explicit param and the claim (per-field manual-skip).
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(50));
        using var scope = provider.CreateScope();
        var repo = Repo(Db(scope));

        var entity = await repo.UpsertAsync(
            new OrderEntity { CreatedByUserID = 999 }, new OrderListDTO { Number = "A-1" }, ActionTypes.Insert,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal(999, entity.CreatedByUserID);   // manual creator kept
        Assert.Equal(50, entity.LastSavedByUserID);  // unset ⇒ filled from current user
    }

    [Fact]
    public async Task Upsert_Insert_Anonymous_LeavesUserIdsNull()
    {
        using var provider = AuditingHost.Build(); // anonymous by default
        using var scope = provider.CreateScope();
        var repo = Repo(Db(scope));

        var entity = await repo.UpsertAsync(
            new OrderEntity(), new OrderListDTO { Number = "A-1" }, ActionTypes.Insert,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Null(entity.CreatedByUserID);
        Assert.Null(entity.LastSavedByUserID);
    }

    // ── Upsert: update ────────────────────────────────────────────────────────

    [Fact]
    public async Task Upsert_Update_AdvancesLastSavedBy_PreservesCreatedBy()
    {
        // CreatedByUserID is insert-only; an update advances LastSavedByUserID to the editor.
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(50));
        using var scope = provider.CreateScope();
        var repo = Repo(Db(scope));

        var entity = await repo.UpsertAsync(
            new OrderEntity(), new OrderListDTO { Number = "A-1" }, ActionTypes.Insert,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);
        Assert.Equal(50, entity.CreatedByUserID);

        entity.AuditFieldsAreSet = false; // mimic a reload (the [NotMapped] guard is not persisted)
        await repo.UpsertAsync(
            entity, new OrderListDTO { Number = "A-1-edited" }, ActionTypes.Update,
            userId: 60, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal(50, entity.CreatedByUserID);    // creator unchanged
        Assert.Equal(60, entity.LastSavedByUserID);  // last-updated-by is the editor
    }

    [Fact]
    public async Task Upsert_Update_LastSavedBy_FallsBackToCurrentUser_WhenUserIdNotPassed()
    {
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(50));
        using var scope = provider.CreateScope();
        var repo = Repo(Db(scope));

        var entity = await repo.UpsertAsync(
            new OrderEntity { CreatedByUserID = 12 }, new OrderListDTO { Number = "A-1" }, ActionTypes.Update,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal(12, entity.CreatedByUserID);    // update never touches the creator
        Assert.Equal(50, entity.LastSavedByUserID);  // editor sourced from the current user
    }

    // ── Delete (the deleter, recorded in LastSavedByUserID) ───────────────────

    [Fact]
    public async Task Delete_RecordsDeleterInLastSavedBy_FromCurrentUser_WhenUserIdNotPassed()
    {
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(50));
        using var scope = provider.CreateScope();
        var repo = Repo(Db(scope));

        var entity = new OrderEntity { Number = "A-1" };
        var before = DateTimeOffset.UtcNow;
        await repo.DeleteAsync(entity, userId: null,
            disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.True(entity.IsDeleted);
        Assert.Equal(50, entity.LastSavedByUserID); // deleter (no dedicated DeletedByUserID column)
        Assert.True(entity.LastSaveDate >= before);
    }

    [Fact]
    public async Task Delete_ExplicitDeleterUserIdWins_OverCurrentUser()
    {
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(50));
        using var scope = provider.CreateScope();
        var repo = Repo(Db(scope));

        var entity = new OrderEntity { Number = "A-1" };
        await repo.DeleteAsync(entity, userId: 9,
            disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.True(entity.IsDeleted);
        Assert.Equal(9, entity.LastSavedByUserID);
    }
}
