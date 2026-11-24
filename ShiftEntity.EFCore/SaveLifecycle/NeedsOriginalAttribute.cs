namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// On a handler class: it reads <see cref="ChangedRow{TEntity}.Original"/> after the write, so the context keeps a
/// copy of every modified and deleted row of its entity types. The copy costs memory for each such row, which is why a
/// handler asks for it.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NeedsOriginalAttribute : Attribute;
