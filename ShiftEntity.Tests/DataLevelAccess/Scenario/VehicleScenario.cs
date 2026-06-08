namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;

/// <summary>
/// The legible companies reused across every data-level-access test. Tiny raw ids stand in for what
/// the real system stores as hashids. The chain is generic on purpose — a <see cref="Distributor"/>
/// sells through an <see cref="Intermediary"/>, which routes to dealers — so it models the same
/// two-leg shape seen across consumers (dealer/intermediary, buyer/seller, from/to) without naming
/// any specific client.
/// </summary>
public static class Companies
{
    public const long DealerA = 1;
    public const long DealerB = 2;
    public const long Distributor = 3;
    public const long Intermediary = 4;
}

/// <summary>Minimal company entity (used from Phase 2 onward for hashid DTO mapping).</summary>
public class Company
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// Two-leg routing entity: <see cref="CompanyID"/> is the dealer leg, <see cref="IntermediaryCompanyID"/>
/// the intermediary leg. The rule wanted is "visible if my company == CompanyID OR my company ==
/// IntermediaryCompanyID" — the cross-column OR within one dimension that today's single-column filters
/// cannot express. The same shape recurs as buyer/seller and from/to columns in other consumers.
/// </summary>
public class Vehicle
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long? CompanyID { get; set; }
    public long? IntermediaryCompanyID { get; set; }

    /// <summary>The user a vehicle is assigned to — illustrates owner dimensions (<c>OnOwner</c>). Unset in the sample data.</summary>
    public long? AssignedUserID { get; set; }
}

public static class VehicleScenario
{
    /// <summary>
    /// Canonical vehicle set — keep identical across slices so tests read consistently. For the
    /// Intermediary user (company 4): CompanyID==4 matches only #3; IntermediaryCompanyID==4 matches
    /// #4/#5/#6; the wanted OR is #3/#4/#5/#6. Single-column filtering on CompanyID alone misses
    /// #4/#5/#6 (the gap v2 closes).
    /// </summary>
    public static List<Vehicle> SampleVehicles() => new()
    {
        new() { Id = 1, Name = "DealerA owned",              CompanyID = Companies.DealerA,     IntermediaryCompanyID = null },
        new() { Id = 2, Name = "DealerB owned",              CompanyID = Companies.DealerB,     IntermediaryCompanyID = null },
        new() { Id = 3, Name = "Intermediary owned",         CompanyID = Companies.Intermediary, IntermediaryCompanyID = null },
        new() { Id = 4, Name = "DealerA via Intermediary",   CompanyID = Companies.DealerA,     IntermediaryCompanyID = Companies.Intermediary },
        new() { Id = 5, Name = "Distributor via Intermediary",CompanyID = Companies.Distributor, IntermediaryCompanyID = Companies.Intermediary },
        new() { Id = 6, Name = "Unassigned via Intermediary",CompanyID = null,                  IntermediaryCompanyID = Companies.Intermediary },
        new() { Id = 7, Name = "Unassigned",                 CompanyID = null,                  IntermediaryCompanyID = null },
    };
}
