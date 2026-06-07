using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.Auditing.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Auditing;

/// <summary>
/// Pins the audit-field behavior of <c>ShiftRepository.SaveChangesAsync</c> — the place every repository routes
/// through to stamp <c>CreateDate</c> / <c>LastSaveDate</c> / <c>CreatedByUserID</c> / <c>LastSavedByUserID</c> /
/// <c>IsDeleted</c> on tracked <c>ShiftEntity&lt;T&gt;</c> rows before EF persists them
/// (<c>ShiftRepository.SetAuditFields</c>, driven by <c>ProcessEntriesAndSave</c>).
///
/// The interesting axis is <b>scope</b>: a single <c>SaveChanges</c> flushes the whole ChangeTracker, so EVERY
/// changed auditable row is stamped — not just the repository's own entity type. This covers cascaded children of a
/// different type, self-referencing (same-type) children, and unrelated entities flushed in the same unit of work.
///
/// <para>This is enabled by <c>ProcessEntriesAndSave</c> filtering tracked entries on the non-generic
/// <c>IShiftEntityAudit</c> interface (implemented by every <c>ShiftEntity&lt;T&gt;</c>) rather than the closed,
/// invariant generic <c>ShiftEntity&lt;EntityType&gt;</c>. Type-specific work (unique-hash, before/after-save hooks,
/// attention, reload) stays gated on the repository's own <c>EntityType</c>.</para>
/// </summary>
public class AuditFieldsTests
{
    private static ShiftRepository<OrderingDbContext, OrderEntity, OrderListDTO, OrderListDTO> OrderRepo(OrderingDbContext db)
        => new(db, new ThrowingMapper<OrderEntity, OrderListDTO>());

    private static ShiftRepository<OrderingDbContext, CategoryEntity, CategoryListDTO, CategoryListDTO> CategoryRepo(OrderingDbContext db)
        => new(db, new ThrowingMapper<CategoryEntity, CategoryListDTO>());

    // ── Root entity: insert ──────────────────────────────────────────────────

    [Fact]
    public async Task Insert_StampsAllAuditFields()
    {
        using var provider = AuditingHost.Build();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var order = new OrderEntity { Number = "A-1" };
        repo.Add(order);

        var before = DateTimeOffset.UtcNow;
        await repo.SaveChangesAsync();
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(order.CreateDate, before, after);
        Assert.InRange(order.LastSaveDate, before, after);
        Assert.False(order.IsDeleted);
        Assert.True(order.AuditFieldsAreSet);
    }

    [Fact]
    public async Task Insert_WithSignedInUser_StampsBothUserIdsWithThatUser()
    {
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(42));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var order = new OrderEntity { Number = "A-1" };
        repo.Add(order);
        await repo.SaveChangesAsync();

        Assert.Equal(42, order.CreatedByUserID);
        Assert.Equal(42, order.LastSavedByUserID);
    }

    [Fact]
    public async Task Insert_Anonymous_LeavesUserIdsNull()
    {
        using var provider = AuditingHost.Build(); // anonymous by default
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var order = new OrderEntity { Number = "A-1" };
        repo.Add(order);
        await repo.SaveChangesAsync();

        Assert.Null(order.CreatedByUserID);
        Assert.Null(order.LastSavedByUserID);
        Assert.True(order.CreateDate > DateTimeOffset.MinValue);
        Assert.True(order.LastSaveDate > DateTimeOffset.MinValue);
    }

    // ── Root entity: update ──────────────────────────────────────────────────

    [Fact]
    public async Task Update_AdvancesLastSave_ButPreservesCreateStamps()
    {
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(7));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var order = new OrderEntity { Number = "A-1" };
        repo.Add(order);
        await repo.SaveChangesAsync();

        var originalCreateDate = order.CreateDate;
        var originalCreatedBy = order.CreatedByUserID;

        // A later save by a different user must not rewrite the create stamps, only the last-save stamps.
        order.AuditFieldsAreSet = false; // mimic a fresh unit-of-work (the guard is reset between operations)
        order.Number = "A-1-edited";
        var beforeUpdate = DateTimeOffset.UtcNow;
        await repo.SaveChangesAsync();

        Assert.Equal(originalCreateDate, order.CreateDate);          // create stamp untouched on update
        Assert.Equal(originalCreatedBy, order.CreatedByUserID);      // creator untouched on update
        Assert.True(order.LastSaveDate > beforeUpdate);             // last-save advanced
    }

    // ── The AuditFieldsAreSet guard ──────────────────────────────────────────

    [Fact]
    public async Task SaveChanges_RespectsAuditFieldsAreSetGuard_AndDoesNotOverwritePreStampedValues()
    {
        // When an upstream operation (UpsertAsync / DeleteAsync) has already stamped and set the guard, the
        // SaveChangesAsync sweep must leave those values alone. We pre-stamp by hand and prove they survive.
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(99));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var stamped = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var order = new OrderEntity
        {
            Number = "A-1",
            CreateDate = stamped,
            LastSaveDate = stamped,
            CreatedByUserID = 1,
            LastSavedByUserID = 1,
            AuditFieldsAreSet = true,
        };
        repo.Add(order);
        await repo.SaveChangesAsync();

        Assert.Equal(stamped, order.CreateDate);
        Assert.Equal(stamped, order.LastSaveDate);
        Assert.Equal(1, order.CreatedByUserID);
        Assert.Equal(1, order.LastSavedByUserID);
    }

    [Fact]
    public async Task SaveChanges_WithGuardSet_SkipsEvenUnsetFields()
    {
        // The guard is the explicit "audit already handled, stamp nothing" signal: with it set, even fields left at
        // their defaults are skipped (this is distinct from the per-field manual-skip below).
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(99));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var order = new OrderEntity { Number = "A-1", AuditFieldsAreSet = true }; // guard set, everything else default
        repo.Add(order);
        await repo.SaveChangesAsync();

        Assert.Equal(default, order.CreateDate);
        Assert.Equal(default, order.LastSaveDate);
        Assert.Null(order.CreatedByUserID);
        Assert.Null(order.LastSavedByUserID);
    }

    // ── Per-field manual values are preserved (set if unset, skip if set) ──────

    [Fact]
    public async Task Insert_PreservesManuallySetAuditFields_AndFillsTheUnsetOnes()
    {
        // The headline behavior: on insert, each audit column is filled from context ONLY where the caller left it
        // unset. A manually-assigned CreateDate / CreatedByUserID is kept; the untouched last-save stamps are filled.
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(50));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var manualCreate = new DateTimeOffset(2021, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var order = new OrderEntity
        {
            Number = "A-1",
            CreateDate = manualCreate,  // set manually
            CreatedByUserID = 999,      // set manually
            // LastSaveDate / LastSavedByUserID left unset
        };
        repo.Add(order);

        var before = DateTimeOffset.UtcNow;
        await repo.SaveChangesAsync();

        Assert.Equal(manualCreate, order.CreateDate);   // manual value kept, not overwritten
        Assert.Equal(999, order.CreatedByUserID);       // manual value kept
        Assert.True(order.LastSaveDate >= before);      // unset ⇒ filled from context
        Assert.Equal(50, order.LastSavedByUserID);      // unset ⇒ filled from the signed-in user
    }

    [Fact]
    public async Task Insert_PreservesManuallySetLastSaveStamps()
    {
        // The same rule applies to the last-save columns on insert: a manually-provided value is kept.
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(50));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var manualLastSave = new DateTimeOffset(2021, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var order = new OrderEntity
        {
            Number = "A-1",
            LastSaveDate = manualLastSave,  // set manually
            LastSavedByUserID = 888,        // set manually
        };
        repo.Add(order);

        var before = DateTimeOffset.UtcNow;
        await repo.SaveChangesAsync();

        Assert.Equal(manualLastSave, order.LastSaveDate);   // manual value kept
        Assert.Equal(888, order.LastSavedByUserID);         // manual value kept
        Assert.True(order.CreateDate >= before);            // unset create stamps filled
        Assert.Equal(50, order.CreatedByUserID);
    }

    [Fact]
    public async Task Update_AlwaysAdvancesLastSave_EvenWhenItWasSetManually()
    {
        // On update there is no reliable way to distinguish a manually-set last-save value from a previously-stamped
        // one, so the update path always advances it (per the agreed semantics).
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(7));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var order = new OrderEntity { Number = "A-1" };
        repo.Add(order);
        await repo.SaveChangesAsync();

        order.AuditFieldsAreSet = false;                                       // fresh unit-of-work
        order.LastSaveDate = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); // a stale/manual value
        order.LastSavedByUserID = 123;
        order.Number = "A-1-edited";
        var beforeUpdate = DateTimeOffset.UtcNow;
        await repo.SaveChangesAsync();

        Assert.True(order.LastSaveDate >= beforeUpdate);    // advanced past the manual value
        Assert.Equal(7, order.LastSavedByUserID);           // set to the current user, not the manual 123
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_SetsIsDeleted_AndStampsLastSave_PreservingCreateStamps()
    {
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(5));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var order = new OrderEntity { Number = "A-1" };
        repo.Add(order);
        await repo.SaveChangesAsync();

        var originalCreateDate = order.CreateDate;
        var originalCreatedBy = order.CreatedByUserID;

        order.AuditFieldsAreSet = false; // mimic a fresh unit-of-work (the [NotMapped] guard is not persisted)
        var beforeDelete = DateTimeOffset.UtcNow;
        await repo.DeleteAsync(order, isHardDelete: false, userId: 5);
        await repo.SaveChangesAsync();

        Assert.True(order.IsDeleted);
        Assert.True(order.LastSaveDate >= beforeDelete);
        Assert.Equal(originalCreateDate, order.CreateDate);
        Assert.Equal(originalCreatedBy, order.CreatedByUserID);
    }

    // ── Child entities of a DIFFERENT type (the headline case) ────────────────

    [Fact]
    public async Task Insert_ParentWithChildrenOfDifferentType_StampsParentAndChildren()
    {
        // Saving an OrderEntity with cascaded OrderLineEntity children stamps audit fields on EVERY changed
        // ShiftEntity row, not just the repository's own type: ProcessEntriesAndSave filters on the non-generic
        // IShiftEntityAudit interface, so the lines (ShiftEntity<OrderLineEntity>) are stamped alongside the parent.
        var before = DateTimeOffset.UtcNow;
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(8));
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

        // Parent: fully stamped.
        Assert.True(order.AuditFieldsAreSet);
        Assert.NotEqual(default, order.CreateDate);
        Assert.Equal(8, order.CreatedByUserID);

        // Children: must be stamped exactly like the parent.
        Assert.All(order.Lines, line =>
        {
            Assert.True(line.AuditFieldsAreSet);
            Assert.True(line.CreateDate >= before);
            Assert.True(line.LastSaveDate >= before);
            Assert.Equal(8, line.CreatedByUserID);
            Assert.Equal(8, line.LastSavedByUserID);
            Assert.False(line.IsDeleted);
        });
    }

    [Fact]
    public async Task Insert_ParentWithChildrenOfDifferentType_StillPersistsChildren()
    {
        // The audit gap does not stop the children from being saved — EF cascades the insert regardless. This
        // separates "are the rows written" (yes) from "are they audited" (no), so the limitation is scoped tightly.
        using var provider = AuditingHost.Build();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var order = new OrderEntity
        {
            Number = "A-1",
            Lines = { new OrderLineEntity { Sku = "SKU-1", Quantity = 2 } },
        };
        repo.Add(order);
        await repo.SaveChangesAsync();

        var persistedLines = db.OrderLines.AsNoTracking().Where(l => l.OrderID == order.ID).ToList();
        Assert.Single(persistedLines);
        Assert.Equal("SKU-1", persistedLines[0].Sku);
    }

    [Fact]
    public async Task Update_ParentAddingNewChildOfDifferentType_StampsTheNewChild()
    {
        // Appending a new line to an existing order and saving through the parent repository stamps the
        // freshly-added child as an inserted, audited row.
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(3));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = OrderRepo(db);

        var order = new OrderEntity { Number = "A-1" };
        repo.Add(order);
        await repo.SaveChangesAsync();

        order.AuditFieldsAreSet = false; // fresh unit-of-work
        var newLine = new OrderLineEntity { Sku = "SKU-NEW", Quantity = 1 };
        order.Lines.Add(newLine);
        var beforeUpdate = DateTimeOffset.UtcNow;
        await repo.SaveChangesAsync();

        Assert.True(newLine.AuditFieldsAreSet);
        Assert.True(newLine.CreateDate > beforeUpdate);
        Assert.Equal(3, newLine.CreatedByUserID);
        Assert.Equal(3, newLine.LastSavedByUserID);
    }

    // ── Child entities of the SAME type (self-reference) ──────────────────────

    [Fact]
    public async Task Insert_ParentWithChildrenOfSameType_StampsParentAndChildren()
    {
        // Contrast: a self-referencing CategoryEntity. The cascaded child IS a ShiftEntity<CategoryEntity>, so the
        // type filter accepts it and the sweep stamps it just like the root.
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(11));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var repo = CategoryRepo(db);

        var root = new CategoryEntity
        {
            Name = "Root",
            Children =
            {
                new CategoryEntity { Name = "Child A" },
                new CategoryEntity { Name = "Child B" },
            },
        };
        repo.Add(root);

        var before = DateTimeOffset.UtcNow;
        await repo.SaveChangesAsync();

        Assert.True(root.AuditFieldsAreSet);
        Assert.All(root.Children, child =>
        {
            Assert.True(child.AuditFieldsAreSet);
            Assert.True(child.CreateDate >= before);
            Assert.Equal(11, child.CreatedByUserID);
            Assert.Equal(11, child.LastSavedByUserID);
            Assert.False(child.IsDeleted);
        });
    }

    // ── Unrelated entities in one unit of work (no navigation relationship) ────
    //
    // A common pattern: a service touches two unrelated aggregates (e.g. an OrderEntity and a CategoryEntity — no FK,
    // no navigation between them) via two repositories sharing one DbContext, then flushes with a SINGLE SaveChanges.
    // Because ProcessEntriesAndSave sweeps the whole ChangeTracker (filtering on IShiftEntityAudit), the behavior is
    // the same as for cascaded children: EVERY changed auditable row is stamped, regardless of which repository's
    // SaveChanges did the flush.

    [Fact]
    public async Task SaveChanges_StampsAuditFieldsOnUnrelatedInsertedEntity()
    {
        // Insert an Order and an unrelated Category in the same unit of work, then flush once through the Order
        // repository. Both are stamped as inserted rows.
        var before = DateTimeOffset.UtcNow;
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(21));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        // Two repositories over the SAME context (the same scope) — one shared ChangeTracker, one flush.
        var orderRepo = OrderRepo(db);
        var categoryRepo = CategoryRepo(db);

        var order = new OrderEntity { Number = "A-1" };
        var category = new CategoryEntity { Name = "Unrelated" };
        orderRepo.Add(order);
        categoryRepo.Add(category);

        // A single flush through ONE of the repositories persists both tracked entities.
        await orderRepo.SaveChangesAsync();

        // The flushing repository's own entity: stamped (sanity).
        Assert.True(order.AuditFieldsAreSet);
        Assert.Equal(21, order.CreatedByUserID);

        // The unrelated entity: must be stamped exactly the same way.
        Assert.True(category.AuditFieldsAreSet);
        Assert.True(category.CreateDate >= before);
        Assert.True(category.LastSaveDate >= before);
        Assert.Equal(21, category.CreatedByUserID);
        Assert.Equal(21, category.LastSavedByUserID);
        Assert.False(category.IsDeleted);
    }

    [Fact]
    public async Task SaveChanges_StampsLastSaveOnUnrelatedModifiedEntity()
    {
        // Modify two unrelated, already-persisted entities and flush once through the Order repository. Both have
        // their last-save stamps advanced.
        using var provider = AuditingHost.Build(() => FakeUserProvider.WithUserId(33));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var orderRepo = OrderRepo(db);
        var categoryRepo = CategoryRepo(db);

        var order = new OrderEntity { Number = "A-1" };
        var category = new CategoryEntity { Name = "Unrelated" };
        orderRepo.Add(order);
        categoryRepo.Add(category);
        await orderRepo.SaveChangesAsync();

        // Fresh unit-of-work: reset the [NotMapped] guard (it is not persisted) and mutate both rows.
        order.AuditFieldsAreSet = false;
        category.AuditFieldsAreSet = false;
        order.Number = "A-1-edited";
        category.Name = "Unrelated-edited";

        var beforeUpdate = DateTimeOffset.UtcNow;
        await orderRepo.SaveChangesAsync();

        Assert.True(order.LastSaveDate > beforeUpdate);    // flushing repo's own entity (sanity)

        Assert.True(category.AuditFieldsAreSet);            // unrelated entity must be stamped too
        Assert.True(category.LastSaveDate > beforeUpdate);
        Assert.Equal(33, category.LastSavedByUserID);
    }
}
