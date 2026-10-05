using ShiftSoftware.TypeAuth.Core.Actions;

namespace ShiftSoftware.ShiftEntity.Core.Pii;

/// <summary>Independent application actions for reveal/changes and partial search.</summary>
public sealed class PiiOptions
{
    /// <summary>Replaces the default permission for reveal and protected replacements or clears.</summary>
    public BooleanAction Action { get; set; } = PiiActionTree.Reveal;
    /// <summary>Replaces the default permission for partial search. Exact lookup needs ordinary read access only.</summary>
    public BooleanAction PartialSearchAction { get; set; } = PiiActionTree.PartialSearch;
    public int VisiblePhoneDigits { get; set; } = 4;
    public string HiddenText { get; set; } = "••••";
}
