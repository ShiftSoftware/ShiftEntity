using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using ShiftSoftware.ShiftEntity.Model.Validation;
using System;
using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Web;

/// <summary>
/// MVC counterpart of <see cref="WrappedValueValidator"/>. The validators of an
/// <see cref="IValidationValueWrapper"/> property check the wrapped value, not the wrapper object.
/// </summary>
internal sealed class WrappedValueModelValidatorProvider : IModelValidatorProvider
{
    public void CreateValidators(ModelValidatorProviderContext context)
    {
        if (context.ModelMetadata.MetadataKind != ModelMetadataKind.Property
            || !typeof(IValidationValueWrapper).IsAssignableFrom(context.ModelMetadata.ModelType))
            return;

        foreach (var result in context.Results)
            if (result.Validator is { } validator)
                result.Validator = new WrappedValueModelValidator(validator);
    }

    private sealed class WrappedValueModelValidator(IModelValidator validator) : IModelValidator
    {
        public IEnumerable<ModelValidationResult> Validate(ModelValidationContext context)
        {
            // A missing wrapper is validated as null, like any other missing member.
            if (context.Model is not IValidationValueWrapper wrapper)
                return validator.Validate(context);
            if (!wrapper.TryGetValidationValue(out var value))
                return Array.Empty<ModelValidationResult>();

            return validator.Validate(new ModelValidationContext(
                context.ActionContext, context.ModelMetadata, context.MetadataProvider, context.Container, value));
        }
    }
}
