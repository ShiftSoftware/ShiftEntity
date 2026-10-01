using ShiftSoftware.ShiftEntity.Model.Validation;

namespace ShiftSoftware.ShiftEntity.Model.Dtos;

/// <summary>
/// A protected field in an ordinary response or an existing form save.
/// The server supplies Display. Value is null in ordinary responses.
/// </summary>
public sealed class PiiFieldDTO : IValidationValueWrapper
{
    public string? Display { get; set; }
    public string? Value { get; set; }
    public string? Write { get; set; }

    // Validation attributes on a protected member describe the raw value.
    // Only "replace" sends a new raw value, so only "replace" is checked.
    // "keep" leaves the stored value as it is, and the mask is never validated.
    bool IValidationValueWrapper.TryGetValidationValue(out object? value)
    {
        value = Value;
        return Write == "replace";
    }
}

public enum PiiKind
{
    Phone,
    Name,
    Email,
    Address,
    Identifier
}

/// <summary>
/// Classifies a DTO member. The entity property remains an ordinary string.
/// Each independently exposed DTO member must be classified separately.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class PiiAttribute(PiiKind kind) : Attribute
{
    public PiiKind Kind { get; } = kind;
    public bool Revealable { get; set; } = true;
}
