namespace ShiftSoftware.ShiftEntity.Model;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class ShiftListNumberFormatterExportAttribute : Attribute
{
    public string Format { get; }

    public ShiftListNumberFormatterExportAttribute(string format)
    {
        Format = format;
    }
}