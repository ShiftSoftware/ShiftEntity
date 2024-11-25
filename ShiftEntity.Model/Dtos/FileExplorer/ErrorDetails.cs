namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public class ErrorDetails
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public IEnumerable<string>? FileExists { get; set; }
}
