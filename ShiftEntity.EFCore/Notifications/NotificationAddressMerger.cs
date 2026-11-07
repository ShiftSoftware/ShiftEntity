using ShiftSoftware.ShiftEntity.Core.Notifications;

namespace ShiftSoftware.ShiftEntity.EFCore.Notifications;

/// <summary>Where an address goes in the email, strongest first.</summary>
internal enum NotificationAddressPlace
{
    To,
    Cc,
    Bcc,
}

/// <summary>
/// Merges everything that matched one event into one email. Addresses are trimmed and compared ignoring case. An
/// address listed twice keeps its strongest place. Anything that isn't a plain address is left out, whatever its source.
/// </summary>
internal sealed class NotificationAddressMerger
{
    private readonly Action<string> onBadAddress;
    private readonly Dictionary<string, (string Address, NotificationAddressPlace Place)> addresses = new(StringComparer.OrdinalIgnoreCase);

    public NotificationAddressMerger(Action<string> onBadAddress)
    {
        this.onBadAddress = onBadAddress;
    }

    public IReadOnlyList<string> To => InPlace(NotificationAddressPlace.To);
    public IReadOnlyList<string> Cc => InPlace(NotificationAddressPlace.Cc);
    public IReadOnlyList<string> Bcc => InPlace(NotificationAddressPlace.Bcc);

    public void AddAll(IEnumerable<string> addresses, NotificationAddressPlace place)
    {
        foreach (var address in addresses)
            Add(address, place);
    }

    public void Add(string address, NotificationAddressPlace place)
    {
        var trimmed = address?.Trim() ?? "";

        if (!NotificationEmailAddress.IsPlainAddress(trimmed))
        {
            onBadAddress(trimmed);
            return;
        }

        if (!addresses.TryGetValue(trimmed, out var existing) || place < existing.Place)
            addresses[trimmed] = (existing.Address ?? trimmed, place);
    }

    private List<string> InPlace(NotificationAddressPlace place)
        => addresses.Values.Where(entry => entry.Place == place).Select(entry => entry.Address).ToList();
}
