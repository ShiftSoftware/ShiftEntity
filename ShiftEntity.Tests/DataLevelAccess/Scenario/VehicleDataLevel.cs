using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;

/// <summary>
/// A ShiftIdentity-agnostic data-level action tree for the test scenario. Mirrors the shape of
/// TypeAuth.Shared's <c>DataLevel</c> tree, but uses a Read/Write/Delete company action so the row path
/// (View=Read, Edit/Insert=Write, Delete=Delete) can be exercised end to end. The class name —
/// <c>VehicleDataLevel</c> — is the access tree's top-level JSON key; the field name <c>Companies</c> is
/// the action key beneath it.
/// </summary>
[ActionTree("Vehicle Data Level", "Data-level dimensions for the Vehicle test scenario.")]
public class VehicleDataLevel
{
    public readonly static DynamicReadWriteDeleteAction Companies = new("Companies");
}
