namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// On a handler class: it is a check that can refuse a save. <see cref="ShiftDbContext.SuppressSaveHandlers(SaveStages)"/>
/// and <see cref="ShiftDbContext.SuppressAllSaveHandlers"/> do not switch it off;
/// <see cref="ShiftDbContext.SuppressSaveHandler{THandler}"/> naming it does.
/// </summary>
/// <remarks>
/// A guard runs in the stages that can still refuse a save: before save, after save and before commit. A class marked
/// with it that implements <see cref="IAfterCommitHandler{TEntity}"/> throws where it is declared, because nothing after
/// the commit can refuse a save. In every other way a guard is a handler like any other: ordered, declared and created
/// the same way, and called with the same rows.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class GuardAttribute : Attribute;
