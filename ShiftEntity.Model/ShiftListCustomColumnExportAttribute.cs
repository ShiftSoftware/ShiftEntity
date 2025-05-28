namespace ShiftSoftware.ShiftEntity.Model;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class ShiftListCustomColumnExportAttribute : Attribute
{
    public string[] Parts { get; }

    public ShiftListCustomColumnExportAttribute(params string[] parts)
    {
        Parts = parts;
    }

    public List<object> ToList()
    {
        var result = new List<object>();

        foreach (var part in Parts)
        {
            if (part.StartsWith("Property."))
            {
                result.Add(new
                {
                    type = "property",
                    value = part.Substring("Property.".Length)
                });
            }
            else
            {
                result.Add(new
                {
                    type = "string",
                    value = part
                });
            }
        }

        return result;
    }
}