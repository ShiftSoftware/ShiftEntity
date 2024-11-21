using System;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core.Extensions;

public static class StringExtension
{
    public static string AddUrlPath(this string? text, params string?[] paths)
    {
        char[] separators = ['/', '\\'];

        text ??= string.Empty;

        var _paths = paths.Where(x => !string.IsNullOrWhiteSpace(x));

        if (_paths.Count() == 0)
        {
            return text;
        }

        var baseAddress = string.IsNullOrWhiteSpace(text) ? "" : text.TrimEnd(separators) + "/";

        _paths = _paths.Select(x => x!.Trim(separators));

        return baseAddress + string.Join("/", _paths);
    }
}
