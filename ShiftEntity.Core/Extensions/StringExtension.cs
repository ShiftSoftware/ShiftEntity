using System;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core.Extensions;

public static class StringExtension
{
    static readonly char[] separators = ['/', '\\'];
    public static string AddUrlPath(this string? text, params string?[] paths)
    {
        text ??= string.Empty;

        var _paths = paths
            .Select(x => x?.Trim(separators))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .SelectMany(x => x!.Split(separators, StringSplitOptions.RemoveEmptyEntries));

        var baseAddress = text.TrimEnd(separators) + "/";

        var url = baseAddress + string.Join("/", _paths);
        return url.Trim(separators);
    }
}
