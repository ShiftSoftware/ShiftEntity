using Microsoft.AspNetCore.Mvc.ModelBinding;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Functions.ModelValidation;

public class ModelValidator
{
    public static ModelStateDictionary Validate(object model)
    {
        var modelState = new ModelStateDictionary();

        var context = new ValidationContext(model, serviceProvider: null, items: null);
        var results = new List<ValidationResult>();

        bool isValid = Validator.TryValidateObject(model, context, results, true);

        if (!isValid)
        {
            foreach (var validationResult in results)
            {
                foreach (var memberName in validationResult.MemberNames)
                {
                    modelState.AddModelError(memberName, validationResult?.ErrorMessage!);
                }
            }
        }

        return modelState;
    }
}
