namespace ShiftSoftware.ShiftEntity.Model.Validation;

/// <summary>
/// A DTO member value that wraps the value its validation attributes describe.
/// <see cref="WrappedValueValidator"/> checks the member's attributes against the wrapped value,
/// not against the wrapper object.
/// </summary>
public interface IValidationValueWrapper
{
    /// <summary>
    /// Gets the value that the member's validation attributes check.
    /// Returns false when the wrapper carries no new value. The member's attributes are then skipped.
    /// </summary>
    bool TryGetValidationValue(out object? value);
}
