using System;
using System.Diagnostics.CodeAnalysis;
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

    // https://github.com/dotnet/aspnetcore/blob/main/src/Mvc/Mvc.Core/src/Routing/UrlHelperBase.cs#L315
    public static bool IsLocalUrl(this string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        // Allows "/" or "/foo" but not "//" or "/\".
        if (url[0] == '/')
        {
            // url is exactly "/"
            if (url.Length == 1)
            {
                return true;
            }

            // url doesn't start with "//" or "/\"
            if (url[1] != '/' && url[1] != '\\')
            {
                return !HasControlCharacter(url.AsSpan(1));
            }

            return false;
        }

        // Allows "~/" or "~/foo" but not "~//" or "~/\".
        if (url[0] == '~' && url.Length > 1 && url[1] == '/')
        {
            // url is exactly "~/"
            if (url.Length == 2)
            {
                return true;
            }

            // url doesn't start with "~//" or "~/\"
            if (url[2] != '/' && url[2] != '\\')
            {
                return !HasControlCharacter(url.AsSpan(2));
            }

            return false;
        }

        return false;

        static bool HasControlCharacter(ReadOnlySpan<char> readOnlySpan)
        {
            // URLs may not contain ASCII control characters.
            for (var i = 0; i < readOnlySpan.Length; i++)
            {
                if (char.IsControl(readOnlySpan[i]))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
