using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Functions.ModelValidation;

public class ModelValidator
{
    public static ModelValidationResult Validate(object model)
    {
        var context = new ValidationContext(model, serviceProvider: null, items: null);
        var results = new List<ValidationResult>();

        bool isValid = Validator.TryValidateObject(model, context, results, true);

        return new ModelValidationResult
        {
            IsValid = isValid,
            Results = results
        };
    }
}
