using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Core.Pii;

/// <summary>Hosts may replace this service to apply their own masking rules.</summary>
public interface IPiiMasker
{
    string? Mask(PiiKind kind, string? rawValue);
}
