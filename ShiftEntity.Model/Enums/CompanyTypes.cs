using System.ComponentModel;

namespace ShiftSoftware.ShiftEntity.Model.Enums;

public enum CompanyTypes
{
    [Description("N/A")]
    NotSpecified = 0,

    [Description("Distributor/Franchisee")]
    DistributorOrFranchisee = 1,

    [Description("Retailer/Dealer")]
    RetailerOrDealer = 2,

    [Description("Supplier/Service Provider")]
    SupplierOrServiceProvider = 3,

    [Description("Third Party/External")]
    ThirdPartyOrExternal = 4,

    [Description("Parent Company/Franchisor")]
    ParentCompanyOrFranchisor = 5,
}
