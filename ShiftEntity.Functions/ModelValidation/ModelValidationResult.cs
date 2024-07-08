using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Functions.ModelValidation;

public class ModelValidationResult
{
    public bool IsValid { get; set; }
    public List<ValidationResult> Results { get; set; } = new();
}
