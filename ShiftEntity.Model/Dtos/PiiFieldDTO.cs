namespace ShiftSoftware.ShiftEntity.Model.Dtos;

/// <summary>
/// A protected field in an ordinary response or an existing form save.
/// The server supplies Display. Value is null in ordinary responses.
/// </summary>
public sealed class PiiFieldDTO
{
    public string? Display { get; set; }
    public string? Value { get; set; }
    public string? Write { get; set; }
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
