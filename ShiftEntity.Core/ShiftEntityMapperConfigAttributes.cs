using System;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Sets the maximum AUTO deep-mapping depth for the source-generated mapper(s) of a triple. Depth = the
/// number of nested child levels below the root that are composed automatically. Absent ⇒ the framework
/// default (<see cref="ShiftEntityMapperDefaults.MaxDepth"/> = 10). Explicit <c>ForXxxChild(ren)</c>
/// still composes beyond the cap.
///
/// Put it on a <c>ShiftRepository&lt;…&gt;</c> subclass (per-repository, the intended spelling), on a
/// <c>[ShiftEntityMapper]</c> partial mapper class, or on the assembly for a compilation-wide default.
/// The fluent equivalent is <c>map.MaxDepth(n)</c> inside <c>UseGeneratedMapper</c>/<c>Configure</c>.
/// The generator reads this at BUILD time.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Assembly, Inherited = false, AllowMultiple = false)]
public sealed class ShiftEntityMapperMaxDepthAttribute : Attribute
{
    public ShiftEntityMapperMaxDepthAttribute(int maxDepth)
    {
        MaxDepth = maxDepth;
    }

    public int MaxDepth { get; }
}

/// <summary>
/// Excludes a property from source-generated mapping. The generator OMITS the member from the generated
/// bodies entirely (no runtime branch) in every direction by default; when the property is a complex
/// child, its whole deep subtree is pruned. The fluent equivalent is <c>map.Ignore(d => d.X)</c> (or the
/// direction-scoped <c>IgnoreView/IgnoreEntity/IgnoreList/IgnoreCopy</c>). The generator reads this at
/// BUILD time.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class ShiftEntityMapperIgnoreAttribute : Attribute
{
}

/// <summary>Framework-wide defaults for source-generated mapping.</summary>
public static class ShiftEntityMapperDefaults
{
    /// <summary>Default automatic deep-mapping depth when no <see cref="ShiftEntityMapperMaxDepthAttribute"/> or <c>map.MaxDepth(n)</c> is set.</summary>
    public const int MaxDepth = 10;
}
