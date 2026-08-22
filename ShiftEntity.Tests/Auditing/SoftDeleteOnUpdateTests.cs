using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.Auditing.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Auditing;

/// <summary>
/// <c>IsDeleted</c> is not writable through an upsert. Deleting is a separate operation behind
/// <c>Access.Delete</c>; an upsert only needs <c>Access.Write</c>, so honouring the flag from a PUT body would
/// make the delete permission bypassable — and in the other direction it would be an undelete, for which the
/// framework exposes no API at all.
/// <para>
/// The guard lives in the REPOSITORY, deliberately. Mappers map every property they are handed, `IsDeleted`
/// included — that is what AutoMapper always did and what generated mappers do since the audit members became
/// mapper payload — and filtering or refusing a write is not mapping's concern. The repository captures the
/// stored flag before mapping and restores it after, so it holds regardless of which mapper is plugged in.
/// </para>
/// <para>
/// This is about the WRITE path only. Reads are untouched: a soft-deleted row still comes back by ID, and a
/// soft-deleted tag still appears on the entities already carrying it. Excluding deleted rows from listings is
/// the repository and OData layer's job, and it happens elsewhere.
/// </para>
/// </summary>
public class SoftDeleteOnUpdateTests
{
    /// <summary>Writes <c>IsDeleted</c> from the DTO — i.e. behaves the way every real mapper now does.</summary>
    private sealed class SoftDeleteWritingMapper : IShiftEntityMapper<OrderEntity, OrderListDTO, OrderListDTO>
    {
        public OrderEntity MapToEntity(OrderListDTO dto, OrderEntity existing, MappingContext context = default)
        {
            existing.Number = dto.Number;
            existing.IsDeleted = dto.IsDeleted;
            return existing;
        }

        public OrderListDTO MapToView(OrderEntity entity, MappingContext context = default) => throw new NotSupportedException();
        public IQueryable<OrderListDTO> MapToList(IQueryable<OrderEntity> query, MappingContext context = default) => throw new NotSupportedException();
        public void CopyEntity(OrderEntity source, OrderEntity target, MappingContext context = default) => throw new NotSupportedException();
    }

    private static ShiftRepository<OrderingDbContext, OrderEntity, OrderListDTO, OrderListDTO> Repo(OrderingDbContext db)
        => new(db, o => o.UseMapper(new SoftDeleteWritingMapper()));

    private static OrderingDbContext Db(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

    [Fact]
    public async Task Update_CannotSoftDelete_ThroughTheUpsertBody()
    {
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(50));
        using var scope = provider.CreateScope();
        var repo = Repo(Db(scope));

        var entity = new OrderEntity { Number = "A-1" };

        var updated = await repo.UpsertAsync(
            entity, new OrderListDTO { Number = "A-2", IsDeleted = true }, ActionTypes.Update,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        // The rest of the payload was mapped — only the member that is a permission, not a value, was skipped.
        Assert.Equal("A-2", updated.Number);
        Assert.False(updated.IsDeleted);
    }

    [Fact]
    public async Task Update_CannotUndelete_ThroughTheUpsertBody()
    {
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(50));
        using var scope = provider.CreateScope();
        var repo = Repo(Db(scope));

        // Already soft-deleted. There is no undelete endpoint in the framework; an upsert must not become one.
        var entity = new OrderEntity { Number = "A-1", IsDeleted = true };

        var updated = await repo.UpsertAsync(
            entity, new OrderListDTO { Number = "A-2", IsDeleted = false }, ActionTypes.Update,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal("A-2", updated.Number);
        Assert.True(updated.IsDeleted);
    }

    [Fact]
    public async Task Insert_IsExempt_BecauseTheAuditStamperForcesTheFlagAnyway()
    {
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(50));
        using var scope = provider.CreateScope();
        var repo = Repo(Db(scope));

        var entity = await repo.UpsertAsync(
            new OrderEntity(), new OrderListDTO { Number = "A-1", IsDeleted = true }, ActionTypes.Insert,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        // Not the repository guard — AuditStamper forces IsDeleted = false on insert. Pinned so the exemption
        // stays a deliberate choice rather than an oversight.
        Assert.False(entity.IsDeleted);
    }

    [Fact]
    public async Task DeleteAsync_StillWorks_TheGuardOnlyCoversUpserts()
    {
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(50));
        using var scope = provider.CreateScope();
        var repo = Repo(Db(scope));

        var entity = new OrderEntity { Number = "A-1" };

        var deleted = await repo.DeleteAsync(entity, userId: null,
            disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        // The point of the guard is to route deletion through the operation that is actually gated on it.
        Assert.True(deleted.IsDeleted);
    }
}
