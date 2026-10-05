namespace ShiftSoftware.ShiftEntity.Core.Phones;

/// <summary>The host must use the same settings for validation, writes and exact lookup.</summary>
public sealed class PhoneNumberOptions
{
    /// <summary>ISO region used for local numbers. Null requires an explicit international number.</summary>
    public string? DefaultRegion { get; set; }
    public PhoneStorageFormat StorageFormat { get; set; } = PhoneStorageFormat.International;
}

public enum PhoneStorageFormat { International, E164 }
