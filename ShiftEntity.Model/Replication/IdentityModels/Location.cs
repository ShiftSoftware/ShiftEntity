using Newtonsoft.Json;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class Location
{
    [JsonProperty("coordinates")]
    public decimal[] Coordinates { get; set; }

    [JsonProperty("type")]
    public string Type => "Point";

    public Location(decimal[] coordinates)
    {
        this.Coordinates = coordinates;
    }
}