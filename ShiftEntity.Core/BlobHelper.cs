using System;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core;

public static class BlobHelper
{
    public const char Delimiter = '/';

    public static bool IsPathDirectory(string? path)
    {
        // empty string is a valid path
        if (path == null || path.StartsWith(Delimiter) || (!string.IsNullOrWhiteSpace(path) && !path.EndsWith(Delimiter)))
            return false;

        return true;
    }

    public static (string dir, string? name) PathAndName(string? path)
    {
        var parts = path?.Split(Delimiter, options: StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts == null || parts.Count == 0)
            return ("", null);

        var name = parts.Last();
        parts.RemoveAt(parts.Count - 1);
        var dir = string.Join(Delimiter, parts);
        dir = string.IsNullOrWhiteSpace(dir) ? "" : dir + Delimiter;

        return (dir, name);
    }

    public static string? GetName(string? path)
    {
        // maybe keep the trailing delimiter when path is a dir
        return path?.Split(Delimiter, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    }

    public static string Combine(params string[] parts)
    {
        if (parts.Length < 2)
            return parts.FirstOrDefault() ?? string.Empty;

        var trimmed = parts.Where(x => !string.IsNullOrEmpty(x)).Select(x => x.Trim(Delimiter));
        var path = string.Join(Delimiter, trimmed);

        if (parts[0].StartsWith(Delimiter))
            path = Delimiter + path;

        if (parts[^1].EndsWith(Delimiter))
            path += Delimiter;

        return path;
    }

    public static string AppendDelimiter(string path, bool prepend = false)
    {
        if (prepend)
            return path.StartsWith(Delimiter) ? path : Delimiter + path;
        return path.EndsWith(Delimiter) ? path : path + Delimiter;
    }
}
