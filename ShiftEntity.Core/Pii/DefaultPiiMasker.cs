using System;
using System.Linq;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Core.Pii;

public sealed class DefaultPiiMasker(IOptions<PiiOptions> options) : IPiiMasker
{
    public string? Mask(PiiKind kind, string? rawValue)
    {
        if (rawValue is null)
            return null;

        var settings = options.Value;
        if (settings.VisiblePhoneDigits < 0)
            throw new InvalidOperationException("VisiblePhoneDigits cannot be negative.");

        return kind switch
        {
            PiiKind.Phone => MaskPhone(rawValue, settings),
            PiiKind.Name => MaskName(rawValue, settings),
            PiiKind.Email => MaskEmail(rawValue, settings),
            PiiKind.Address or PiiKind.Identifier => settings.HiddenText,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static string MaskPhone(string value, PiiOptions settings)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        var visible = settings.VisiblePhoneDigits > 0 && digits.Length > settings.VisiblePhoneDigits
            ? digits[^settings.VisiblePhoneDigits..]
            : string.Empty;
        return visible.Length == 0 ? settings.HiddenText : $"{settings.HiddenText} {visible}";
    }

    private static string MaskName(string value, PiiOptions settings)
    {
        var initials = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part[0])
            .ToArray();
        return initials.Length == 0 ? settings.HiddenText : string.Join(" ", initials);
    }

    private static string MaskEmail(string value, PiiOptions settings)
    {
        var at = value.IndexOf('@');
        if (at <= 0 || at == value.Length - 1)
            return settings.HiddenText;
        return $"{value[0]}{settings.HiddenText}@{value[(at + 1)..]}";
    }
}
