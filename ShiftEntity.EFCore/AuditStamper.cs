using System;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Flags;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// The shared audit-stamping rules, used by both <see cref="ShiftRepository{DB,EntityType,ListDTO,ViewAndUpsertDTO}"/>
/// (its SaveChanges sweep) and <see cref="ShiftDbContext"/> (its SaveChanges override — the fallback for saves that
/// do not go through a repository). Centralizing them keeps the two paths stamping identically.
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
    /// Insert-only backfill of the org/location claim columns, each only where the entity left it unset.
    /// </summary>
    public static void StampCreationClaims(
        object entity, long? countryId, long? regionId, long? cityId, long? companyId, long? companyBranchId)
    {
        if (entity is IEntityHasCountry country && country.CountryID is null)
            country.CountryID = countryId;

        if (entity is IEntityHasRegion region && region.RegionID is null)
            region.RegionID = regionId;

        if (entity is IEntityHasCity city && city.CityID is null)
            city.CityID = cityId;

        if (entity is IEntityHasCompany company && company.CompanyID is null)
            company.CompanyID = companyId;

        if (entity is IEntityHasCompanyBranch branch && branch.CompanyBranchID is null)
            branch.CompanyBranchID = companyBranchId;
    }
}
