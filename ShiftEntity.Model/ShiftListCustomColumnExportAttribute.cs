namespace ShiftSoftware.ShiftEntity.Model;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class CustomColumnExportAttribute : Attribute
{
    public string Format { get; }
    public string[] Args { get; }

    public CustomColumnExportAttribute(string format, params string[] args)
    {
        Args = args;
        Format = format;
    }
}