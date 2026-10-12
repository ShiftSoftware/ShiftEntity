namespace ShiftSoftware.ShiftEntity.Model;

/// <summary>
/// Declares, once on a value type, which of its members a list export writes.
/// A column whose member has this type exports only the named member; when that member
/// is not a string the cell is blank, and no other member is ever read.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public sealed class ExportValueMemberAttribute(string memberName) : Attribute
{
    public string MemberName { get; } = memberName;
}
