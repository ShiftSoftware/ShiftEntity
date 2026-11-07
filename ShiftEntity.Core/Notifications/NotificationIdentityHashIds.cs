using ShiftSoftware.ShiftEntity.Model.HashIds;
using System;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>Reads the Identity hash IDs that a subscribers entry holds.</summary>
public static class NotificationIdentityHashIds
{
    public static readonly JsonHashIdConverterAttribute User = new UserHashIdConverter();
    public static readonly JsonHashIdConverterAttribute Team = new TeamHashIdConverter();
    public static readonly JsonHashIdConverterAttribute Company = new CompanyHashIdConverter();
    public static readonly JsonHashIdConverterAttribute Branch = new CompanyBranchHashIdConverter();
    public static readonly JsonHashIdConverterAttribute Brand = new BrandHashIdConverter();
    public static readonly JsonHashIdConverterAttribute Region = new RegionHashIdConverter();
    public static readonly JsonHashIdConverterAttribute City = new CityHashIdConverter();
    public static readonly JsonHashIdConverterAttribute Country = new CountryHashIdConverter();

    /// <summary>
    /// Decodes a hash ID. Returns false for text that isn't one. <see cref="IHashIdService"/> gives 0 for a hash it
    /// can't decode, and no record has that ID. It throws on such text when the hash settings aren't registered.
    /// </summary>
    public static bool TryDecode(this IHashIdService hashIds, string? hash, JsonHashIdConverterAttribute kind, out long id)
    {
        id = 0;

        if (string.IsNullOrWhiteSpace(hash))
            return false;

        try
        {
            id = hashIds.Decode(hash, kind);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            return false;
        }

        return id > 0;
    }
}
