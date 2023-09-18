namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public class ShiftFileDTO
{
    public string? Name { get; set; }
    public string? Blob { get; set; }
    public string? Url { get; set; }
    public string? ContentType { get; set; }
    public long Size { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}
