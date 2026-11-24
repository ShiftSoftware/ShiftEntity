namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// The stages of a save, to name the ones whose handlers a switch turns off
/// (<see cref="ShiftDbContext.SuppressSaveHandlers(SaveStages)"/>).
/// </summary>
[Flags]
public enum SaveStages
{
    None = 0,
    BeforeSave = 1,
    AfterSave = 2,
    BeforeCommit = 4,
    AfterCommit = 8,
}
