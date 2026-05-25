namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public interface IHasAttentionSummary
{
    bool HasActiveAttention { get; set; }
    int? HighestSeverity { get; set; }
    int ActiveSignalCount { get; set; }
}
