using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;

namespace ShiftSoftware.ShiftEntity.Core;

[ActionTree("PII", "Protected personal information")]
public class PiiActionTree
{
    public readonly static BooleanAction Reveal = new("Reveal or change protected information");
    public readonly static BooleanAction PartialSearch = new("Search protected information using partial values");
}
