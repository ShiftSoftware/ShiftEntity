using ShiftSoftware.TypeAuth.Core.Actions;

namespace ShiftSoftware.ShiftEntity.Core.Pii;

/// <summary>One action governs reveal and protected replacements or clears throughout an application.</summary>
public sealed class PiiOptions
{
    public BooleanAction Action { get; set; } = PiiActionTree.Reveal;
    public int VisiblePhoneDigits { get; set; } = 4;
    public string HiddenText { get; set; } = "••••";
}
