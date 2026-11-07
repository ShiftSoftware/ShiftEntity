using ShiftSoftware.ShiftEntity.Model.Flags;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.ShiftEntity.Model.Notifications;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>
/// Decides whether a <see cref="NotificationWhere"/> matches a record, using the markers the record already declares.
/// For a user it only narrows what the user hears, and the access checks still apply. For an address it is the only
/// check on the record, so anything it can't read doesn't match.
/// </summary>
public sealed class NotificationWhereMatcher
{
    private readonly IHashIdService hashIds;

    public NotificationWhereMatcher(IHashIdService hashIds)
    {
        this.hashIds = hashIds;
    }

    /// <param name="where">The <c>where</c> to check.</param>
    /// <param name="record">The record. <typeparamref name="TEntity"/> is its own type, which the markers name.</param>
    public bool Matches<TEntity>(NotificationWhere where, TEntity record) where TEntity : class
    {
        if (where.UnreadableParts.Count > 0)
            return false;

        if (where.IsAll)
            return !where.HasKeys;

        return where.HasKeys
            && KeyMatches(where.Company, NotificationIdentityHashIds.Company, (record as IEntityHasCompany<TEntity>)?.CompanyID)
            && KeyMatches(where.Branch, NotificationIdentityHashIds.Branch, (record as IEntityHasCompanyBranch<TEntity>)?.CompanyBranchID)
            && KeyMatches(where.Brand, NotificationIdentityHashIds.Brand, (record as IEntityHasBrand<TEntity>)?.BrandID)
            && KeyMatches(where.Region, NotificationIdentityHashIds.Region, (record as IEntityHasRegion<TEntity>)?.RegionID)
            && KeyMatches(where.City, NotificationIdentityHashIds.City, (record as IEntityHasCity<TEntity>)?.CityID)
            && KeyMatches(where.Country, NotificationIdentityHashIds.Country, (record as IEntityHasCountry<TEntity>)?.CountryID)
            && KeyMatches(where.Team, NotificationIdentityHashIds.Team, (record as IEntityHasTeam<TEntity>)?.TeamID);
    }

    // A key that isn't set says nothing. A key that is set needs a record value equal to it.
    private bool KeyMatches(string? hash, JsonHashIdConverterAttribute kind, long? recordValue)
    {
        if (hash is null)
            return true;

        return recordValue is not null
            && hashIds.TryDecode(hash, kind, out var id)
            && id == recordValue;
    }
}
