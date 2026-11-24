using Microsoft.EntityFrameworkCore.ChangeTracking;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Flags;
using System.Collections.Concurrent;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// The audit-stamping rules. <see cref="ShiftDbContext"/> applies them in every save, to the rows that are new in each
/// round of its before-save stage: a new row is filled where it was left unset, and on a repository's save a modified
/// row gets its last-save columns too. <see cref="ShiftRepository{DB,EntityType,ListDTO,ViewAndUpsertDTO}"/> applies
/// them to the record it maps, before the save, and so does any other update path that opts into stamping explicitly
/// (e.g. <c>AttentionPipeline.ClearSignals</c>). Centralizing them keeps every path stamping identically.
/// </summary>
internal static class AuditStamper
{
    /// <summary>
    /// Per-field stamp of the date / user / soft-delete columns: on insert each is filled only where the caller left
    /// it unset (a manually-assigned value wins); on update the last-save columns always advance. Honors the
    /// <c>AuditFieldsAreSet</c> guard. Returns <see langword="true"/> when it actually stamped (the guard was not
    /// already set), so the caller can pair it with the insert-only claim backfill.
    /// </summary>
    public static bool StampAuditFields(IShiftEntityAudit entity, bool isAdded, long? userId, DateTimeOffset now)
    {
        if (entity.AuditFieldsAreSet)
            return false;

        if (isAdded)
        {
            if (entity.CreateDate == default)
                entity.CreateDate = now;

            if (entity.CreatedByUserID is null)
                entity.CreatedByUserID = userId;

            entity.IsDeleted = false;

            if (entity.LastSaveDate == default)
                entity.LastSaveDate = now;

            if (entity.LastSavedByUserID is null)
                entity.LastSavedByUserID = userId;
        }
        else
        {
            entity.LastSaveDate = now;
            entity.LastSavedByUserID = userId;
        }

        entity.AuditFieldsAreSet = true;
        return true;
    }

    /// <summary>
    /// The stamp of a row that a save is about to write as modified: its last-save columns advance. They are written
    /// through EF's entry, so the change is marked at once and is written whether or not change detection runs again
    /// before the write, also when the app has switched automatic detection off. Honors the
    /// <c>AuditFieldsAreSet</c> guard.
    /// </summary>
    public static bool StampLastSave(EntityEntry entry, IShiftEntityAudit entity, long? userId, DateTimeOffset now)
    {
        if (entity.AuditFieldsAreSet)
            return false;

        var metadata = entry.Metadata;

        // A column the model does not map as the interface names it is set on the record itself.
        if (metadata.FindProperty(nameof(IShiftEntityAudit.LastSaveDate)) is { } lastSaveDate && lastSaveDate.ClrType == typeof(DateTimeOffset))
            entry.Property(lastSaveDate).CurrentValue = now;
        else
            entity.LastSaveDate = now;

        if (metadata.FindProperty(nameof(IShiftEntityAudit.LastSavedByUserID)) is { } lastSavedBy && lastSavedBy.ClrType == typeof(long?))
            entry.Property(lastSavedBy).CurrentValue = userId;
        else
            entity.LastSavedByUserID = userId;

        entity.AuditFieldsAreSet = true;
        return true;
    }

    /// <summary>
    /// Insert-only backfill of the org/location claim columns, each only where the entity left it unset.
    /// How a column is reached is <see cref="ClaimColumn"/>'s concern.
    /// </summary>
    public static void StampCreationClaims(
        object entity, long? countryId, long? regionId, long? cityId, long? companyId, long? companyBranchId)
    {
        country.SetIfUnset(entity, countryId);
        region.SetIfUnset(entity, regionId);
        city.SetIfUnset(entity, cityId);
        company.SetIfUnset(entity, companyId);
        companyBranch.SetIfUnset(entity, companyBranchId);
    }

    // One ClaimColumn per marker. The property names go through nameof on an arbitrary closed form of the
    // marker (nameof is compile-time only, so the type argument never matters) — rename-safe, no magic strings.
    private static readonly ClaimColumn country = new(typeof(IEntityHasCountry<>), nameof(IEntityHasCountry<object>.CountryID));
    private static readonly ClaimColumn region = new(typeof(IEntityHasRegion<>), nameof(IEntityHasRegion<object>.RegionID));
    private static readonly ClaimColumn city = new(typeof(IEntityHasCity<>), nameof(IEntityHasCity<object>.CityID));
    private static readonly ClaimColumn company = new(typeof(IEntityHasCompany<>), nameof(IEntityHasCompany<object>.CompanyID));
    private static readonly ClaimColumn companyBranch = new(typeof(IEntityHasCompanyBranch<>), nameof(IEntityHasCompanyBranch<object>.CompanyBranchID));

    /// <summary>
    /// One org/location claim column, declared by its generic marker interface (e.g. <c>IEntityHasCompany&lt;T&gt;</c>
    /// declares <c>CompanyID</c>). The markers are deliberately generic-only, while the SaveChanges sweep meets
    /// entities of arbitrary types (cascaded children, unrelated rows in the same unit of work) — so the column is
    /// reached through the marker's <see cref="PropertyInfo"/>, looked up once per entity type and cached (a type
    /// without the marker caches a null and is skipped). Reading and writing through the interface's property behaves
    /// exactly like a cast: explicit implementations work too.
    /// </summary>
    /// <remarks>
    /// Cost: the cached reflective get/set is ~50ns per column per row — noise next to the database round-trip the
    /// sweep precedes.
    /// </remarks>
    private sealed class ClaimColumn
    {
        private readonly Type markerDefinition;
        private readonly string propertyName;
        private readonly ConcurrentDictionary<Type, PropertyInfo?> propertyPerEntityType = new();
        private readonly Func<Type, PropertyInfo?> findMarkerProperty; // cached once so the lookup allocates nothing

        /// <param name="markerDefinition">The open generic marker, e.g. <c>typeof(IEntityHasCompany&lt;&gt;)</c>.</param>
        /// <param name="propertyName">The column that marker declares, e.g. <c>CompanyID</c>.</param>
        public ClaimColumn(Type markerDefinition, string propertyName)
        {
            this.markerDefinition = markerDefinition;
            this.propertyName = propertyName;
            this.findMarkerProperty = FindMarkerProperty;
        }

        /// <summary>Fills the column on a marked entity, only where it was left unset.</summary>
        public void SetIfUnset(object entity, long? value)
        {
            var property = propertyPerEntityType.GetOrAdd(entity.GetType(), findMarkerProperty);

            if (property is null)
                return; // this entity does not carry the marker

            if (property.GetValue(entity) is not null)
                return; // a manually-assigned value wins

            property.SetValue(entity, value);
        }

        /// <summary>The marker's property on this entity type, under whatever type argument the entity closed it.</summary>
        private PropertyInfo? FindMarkerProperty(Type entityType)
        {
            return entityType.GetInterfaces()
                .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == markerDefinition)
                ?.GetProperty(propertyName);
        }
    }
}
