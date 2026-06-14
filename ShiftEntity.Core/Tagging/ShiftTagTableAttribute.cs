using System;

namespace ShiftSoftware.ShiftEntity.Core.Tagging;

/// <summary>
/// Overrides the auto-generated many-to-many join table name for a taggable entity.
/// Default name is <c>{EntityClrName}Tags</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ShiftTagTableAttribute : Attribute
{
    public string Name { get; }
    public string? Schema { get; }

    public ShiftTagTableAttribute(string name, string? schema = null)
    {
        Name = name;
        Schema = schema;
    }
}
