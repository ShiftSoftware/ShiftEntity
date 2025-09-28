using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public class LogItem
{
    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    public string? Action { get; set; }
    public string? Path { get; set; }
    public string? Container { get; set; }
    public string? AccountName { get; set; }
    public DateTime Timestamp { get; set; }
    public long? CompanyID { get; set; }
    public string? CompanyHashedID { get; set; }
    public long? CompanyBranchID { get; set; }
    public string? CompanyBranchHashedID { get; set; }
    public long? UserID { get; set; }
    public string? UserHashedID { get; set; }
}
