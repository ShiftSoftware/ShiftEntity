using System;


namespace ShiftSoftware.ShiftEntity.Core;

public class ShiftEntityValueTextAttribute : Attribute
{
    public ShiftEntityValueTextAttribute(string value, string text)
    {
        Value = value;
        Text = text;
    }

    public string Value { get; set; } = default!;
    public string Text { get; set; } = default!;
}
