using System;
using System.Collections.Generic;
using System.Text;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class CityRegionModel : ReplicationModel
{
    public string CountryID { get; set; } = default!;
    public string RegionID { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? IntegrationId { get; set; } = default!;
    public string? ShortCode { get; set; }
    public bool BuiltIn { get; set; }

    public CountryModel Country { get; set; }
}
