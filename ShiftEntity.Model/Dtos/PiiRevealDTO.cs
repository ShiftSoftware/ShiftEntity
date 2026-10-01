namespace ShiftSoftware.ShiftEntity.Model.Dtos;

/// <summary>Only an explicit one-field reveal response may carry a raw protected value.</summary>
public sealed class PiiRevealDTO
{
    public string? Value { get; set; }
}
