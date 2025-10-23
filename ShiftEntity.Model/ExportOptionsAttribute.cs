namespace ShiftSoftware.ShiftEntity.Model;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class ExportOptionsAttribute : Attribute
{
    public bool Hidden { get; }
    public string? Format { get; }
    public string[]? Args { get; }

    public ExportOptionsAttribute(bool hidden)
    {
        Hidden = hidden;
    }

    /// <summary>
    /// NumberFormatter.
    /// </summary>
    /// <param name="format"></param>
    public ExportOptionsAttribute(string format)
    {
        Format = format;
    }

    /// <summary>
    /// CustomColumn Formatter.
    /// </summary>
    /// <param name="format"></param>
    /// <param name="args"></param>
    public ExportOptionsAttribute(string format, params string[] args)
    {
        Format = format;
        Args = args;
    }
}
