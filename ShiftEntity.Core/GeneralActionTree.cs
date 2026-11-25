namespace ShiftSoftware.ShiftEntity.Core;

using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;

[ActionTree("General", "General Actions")]
public class GeneralActionTree
{
    public readonly static BooleanAction DataGridExport = new("Data Grid Export", null);
    public readonly static DecimalAction DataGridMaxTop = new("Data Grid Max Top", null, 5, int.MaxValue);

    /// <summary>
    /// The most rows a user can write in one action on a data grid: a ShiftList action button that changes the selected
    /// rows, or a custom data grid that saves many rows in one request.
    /// </summary>
    /// <remarks>
    /// This is not a general write limit: form saves, imports and background jobs don't use it. The limit for reading
    /// rows is <see cref="DataGridMaxTop"/>. ShiftList's ActionButton stops a bigger selection before it is sent. The
    /// endpoint behind the action has to ask for the check: it loads the selection with
    /// GetSelectedEntitiesForBulkWriteAsync, or calls
    /// <see cref="DataGridBulkWriteExtensions.EnsureDataGridBulkWriteMaxTop"/> with the number of rows a custom data grid
    /// sent. 0 allows no bulk write.
    /// </remarks>
    public readonly static DecimalAction DataGridBulkWriteMaxTop = new("Data Grid Bulk Write Max Top", null, 5, int.MaxValue);
}