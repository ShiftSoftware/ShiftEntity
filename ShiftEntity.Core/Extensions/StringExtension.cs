using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Extensions;

public static class StringExtension
{
    public static string AddUrlPath(this string text, params string?[] paths)
    {
        char[] separators = ['/', '\\'];

        if (string.IsNullOrWhiteSpace(text)) return text;

        var _paths = paths.Where(x => !string.IsNullOrWhiteSpace(x));

        if (_paths.Count() == 0)
        {
            return text;
        }

        var baseAddress = text.TrimEnd(separators);

        _paths = _paths.Select(x => x!.TrimStart(separators).TrimEnd(separators));

        return baseAddress + "/" + string.Join("/", _paths);
    }
}
