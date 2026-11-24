using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Lists the types of an app's or a module's assembly for the scans the framework makes when a host starts:
/// repositories, endpoints, DTOs and mappers.
/// </summary>
public static partial class AssemblyScan
{
    // How many missing pieces the message lists, so that one broken assembly does not make a page of text.
    private const int Listed = 10;

    /// <summary>
    /// Every type of the assembly. When a type of it cannot be loaded, because an assembly it needs is missing or no
    /// longer has what it was built against, this throws an <see cref="InvalidOperationException"/> that names the
    /// assembly, its version and what is missing, and says what to do: update an assembly built for an older version of
    /// the framework, or deploy what it needs. It does so instead of a bare <see cref="ReflectionTypeLoadException"/>.
    /// The loader's exception is its inner exception.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    public static Type[] TypesOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            throw new InvalidOperationException(Describe(assembly, exception), exception);
        }
    }

    private static string Describe(Assembly assembly, ReflectionTypeLoadException exception)
    {
        var name = NameOf(assembly);

        var missing = exception.LoaderExceptions
            .Select(Missing)
            .Where(piece => piece is not null)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var shown = missing.Count == 0
            ? "something that this application does not have"
            : string.Join("; ", missing.Take(Listed)) + (missing.Count > Listed ? $"; and {missing.Count - Listed} more" : "");

        // The loader cannot tell why a piece is missing, so the message names both causes.
        return $"The types of {name.Name} {name.Version} cannot be loaded, so the framework cannot scan it when the host starts. " +
            $"They need {shown}. {name.Name} may be built for an older version of the Shift Framework, whose packages brought what " +
            "it needs or had what it was built against: then update it to a version built for this one. Or something it needs may " +
            "not be deployed with the application: then deploy it.";
    }

    private static (string Name, string Version) NameOf(Assembly assembly)
    {
        try
        {
            var name = assembly.GetName();
            return (name.Name ?? assembly.ToString(), name.Version?.ToString() ?? "(no version)");
        }
        catch (Exception)
        {
            return (assembly.FullName ?? assembly.ToString(), "(no version)");
        }
    }

    // What one loader exception says is missing: an assembly, or a type that an assembly no longer has.
    private static string? Missing(Exception? exception) => exception switch
    {
        null => null,
        FileNotFoundException { FileName: { Length: > 0 } file } => $"the assembly {file}",
        FileLoadException { FileName: { Length: > 0 } file } => $"the assembly {file}",
        TypeLoadException typeLoad when TypeFromAssembly().Match(typeLoad.Message) is { Success: true } match =>
            $"the type {match.Groups[1].Value} from {match.Groups[2].Value}",
        TypeLoadException { TypeName: { Length: > 0 } type } => $"the type {type}",
        _ => exception.Message,
    };

    // The loader's own wording: "Could not load type 'Name' from assembly 'Assembly, Version=…'".
    [GeneratedRegex(@"type '([^']+)' from assembly '([^']+)'")]
    private static partial Regex TypeFromAssembly();
}
