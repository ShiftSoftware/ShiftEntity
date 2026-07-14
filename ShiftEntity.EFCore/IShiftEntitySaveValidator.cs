using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// A cross-cutting validation hook invoked by the built-in
/// <see cref="ShiftRepository{DB, EntityType, ListDTO, ViewAndUpsertDTO}"/> immediately before it persists a unit
/// of work. It runs on the <b>repository save path only</b> (<c>repository.SaveChangesAsync()</c>) — not on direct
/// <c>DbContext.SaveChanges</c> calls such as seeding, replication, or migrations — preserving the semantics of a
/// per-repository guard while removing the need to write one.
/// <para>
/// Every registered validator runs on every repository save (including custom repositories, which reach this by
/// calling <c>base.SaveChangesAsync()</c>). Throw a <c>ShiftEntityException</c> to abort the save before anything
/// is written. Register the implementation in DI (scoped or singleton). This is how a host enforces save-time
/// rules that span many entities — e.g. per-feature locking — without a per-entity repository override.
/// </para>
/// </summary>
public interface IShiftEntitySaveValidator
{
    /// <summary>
    /// Called with the change-tracker entries this save will persist (states Added, Modified, or Deleted). Inspect
    /// them and throw to reject the save; return to allow it.
    /// </summary>
    void Validate(IReadOnlyList<EntityEntry> pendingWrites);
}
