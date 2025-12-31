namespace ShiftSoftware.ShiftEntity.Core;

using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;

[ActionTree("General", "General Actions")]
public class GeneralActionTree
{
    public readonly static BooleanAction DataGridExport = new("Data Grid Export", null);
    public readonly static DecimalAction DataGridMaxTop = new("Data Grid Max Top", null, 5, int.MaxValue);
}