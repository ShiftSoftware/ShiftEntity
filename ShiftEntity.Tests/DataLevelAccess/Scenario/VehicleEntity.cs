using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;

/// <summary>
/// <see cref="ShiftEntity{EntityType}"/>-derived twin of the scenario <see cref="Vehicle"/> POCO — the repository
/// stack (<see cref="ShiftSoftware.ShiftEntity.EFCore.ShiftRepositoryOptions{EntityType}"/>,
/// <c>ShiftRepository</c>) constrains its entity to <c>ShiftEntity&lt;T&gt;</c>, which the engine-level tests'
/// POCO deliberately isn't. Same two company legs, seeded from the same sample set. A top-level class because the
/// repository tests also map it through EF (which does not support nested entity types).
/// </summary>
public class VehicleEntity : ShiftEntity<VehicleEntity>
{
    public string Name { get; set; } = "";
    public long? CompanyID { get; set; }
    public long? IntermediaryCompanyID { get; set; }

    /// <summary>The user a vehicle is assigned to — illustrates owner dimensions (<c>OnOwner</c>).</summary>
    public long? AssignedUserID { get; set; }

    /// <summary>The canonical sample set (<see cref="VehicleScenario.SampleVehicles"/>) as entities.</summary>
    public static List<VehicleEntity> FromSamples()
        => VehicleScenario.SampleVehicles()
            .Select(v => new VehicleEntity
            {
                ID = v.Id,
                Name = v.Name,
                CompanyID = v.CompanyID,
                IntermediaryCompanyID = v.IntermediaryCompanyID,
                AssignedUserID = v.AssignedUserID,
            })
            .ToList();
}
