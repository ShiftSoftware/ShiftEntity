using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Model.Validation;

/// <summary>
/// DataAnnotations validation that understands <see cref="IValidationValueWrapper"/> members.
/// The attributes of a wrapper member check the wrapped value. Everything else is validated by <see cref="Validator"/>.
/// </summary>
public static class WrappedValueValidator
{
    private static readonly ConcurrentDictionary<Type, bool> typesWithWrapperMembers = new();

    /// <summary>
    /// Same as <see cref="Validator.TryValidateObject(object, ValidationContext, ICollection{ValidationResult}, bool)"/>
    /// with all properties validated.
    /// </summary>
    public static bool TryValidateObject(object instance, ValidationContext context, ICollection<ValidationResult> results)
    {
        if (!typesWithWrapperMembers.GetOrAdd(instance.GetType(), HasWrapperMembers))
            return Validator.TryValidateObject(instance, context, results, validateAllProperties: true);

        // Validator cannot unwrap a single member, so this repeats its three steps in the same order:
        // the properties, then the attributes on the type, then IValidatableObject.
        // The last two steps run only when the earlier steps found no errors.
        var valid = true;
        foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(instance.GetType()))
        {
            var propertyContext = new ValidationContext(instance, context, context.Items) { MemberName = property.Name };
            if (!TryValidateProperty(property.GetValue(instance), propertyContext, results))
                valid = false;
        }
        if (!valid)
            return false;

        var typeAttributes = TypeDescriptor.GetAttributes(instance.GetType()).OfType<ValidationAttribute>();
        if (!Validator.TryValidateValue(instance, context, results, typeAttributes))
            return false;

        if (instance is IValidatableObject validatable)
        {
            foreach (var result in validatable.Validate(context) ?? Enumerable.Empty<ValidationResult>())
            {
                if (result == ValidationResult.Success)
                    continue;
                results.Add(result);
                valid = false;
            }
        }
        return valid;
    }

    /// <summary>
    /// Same as <see cref="Validator.TryValidateProperty(object, ValidationContext, ICollection{ValidationResult})"/>.
    /// When the value is a wrapper, the member's attributes check the wrapped value instead.
    /// </summary>
    public static bool TryValidateProperty(object? value, ValidationContext context, ICollection<ValidationResult> results)
    {
        if (value is not IValidationValueWrapper wrapper)
            return Validator.TryValidateProperty(value, context, results);
        if (!wrapper.TryGetValidationValue(out var wrappedValue))
            return true;

        var attributes = TypeDescriptor.GetProperties(context.ObjectType)[context.MemberName!]?.Attributes
            .OfType<ValidationAttribute>() ?? Enumerable.Empty<ValidationAttribute>();
        return Validator.TryValidateValue(wrappedValue, context, results, attributes);
    }

    private static bool HasWrapperMembers(Type type)
        => TypeDescriptor.GetProperties(type).Cast<PropertyDescriptor>()
            .Any(property => typeof(IValidationValueWrapper).IsAssignableFrom(property.PropertyType));
}
