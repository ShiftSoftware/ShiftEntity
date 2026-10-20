using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

/// <summary>
/// A branch's document in the <see cref="IdentityDatabaseAndContainerNames.BranchContainerName"/> container, one per
/// branch, partitioned by <c>/id</c>. It carries everything <see cref="CompanyBranchModel"/> carries, with the city's
/// region and country and the company always filled, and the services, departments and brands the branch offers.
/// Replication rebuilds it from SQL after every save that changes any of those rows.
/// </summary>
/// <remarks>
/// The lists leave out a link that is deleted, and a service, department or brand that is deleted. They are ordered
/// by ID. <see cref="CompanyBranchModel.BranchID"/> and <see cref="CompanyBranchModel.ItemType"/> are filled as in
/// the <c>CompanyBranches</c> container, so a reader that moves here keeps its queries.
/// </remarks>
public class BranchModel : CompanyBranchModel
{
    public List<BranchItemModel> Services { get; set; } = new();
    public List<BranchItemModel> Departments { get; set; } = new();
    public List<BranchItemModel> Brands { get; set; } = new();
}

/// <summary>A service, department or brand a branch offers, as its <see cref="BranchModel"/> lists it.</summary>
public class BranchItemModel
{
    public long ID { get; set; }
    public string? IntegrationId { get; set; }
    public string Name { get; set; } = default!;
}
