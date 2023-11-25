
namespace ShiftSoftware.ShiftEntity.Model;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class ShiftEntityKeyAndNameAttribute : Attribute
{
    public ShiftEntityKeyAndNameAttribute(string value, string text)
    {
        Value = value;
        Text = text;
    }

    public string Value { get; set; } = default!;
    public string Text { get; set; } = default!;
}