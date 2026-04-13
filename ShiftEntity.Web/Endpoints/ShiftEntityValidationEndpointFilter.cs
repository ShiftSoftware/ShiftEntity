using Microsoft.AspNetCore.Http;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Endpoints;

/// <summary>
/// Minimal-API counterpart of the controller pipeline's
/// <c>InvalidModelStateResponseFactory</c> (wired up in
/// <c>IMvcBuilderExtensions.AddShiftEntityWeb</c>). Runs DataAnnotations validation on
/// every non-null <see cref="ShiftEntityViewAndUpsertDTO"/> argument of the endpoint
/// and, on failure, short-circuits with the exact same <see cref="ShiftEntityResponse{T}"/>
/// body the MVC factory produces — so clients see byte-compatible responses whether
/// they hit the controller path or the minimal API path.
/// </summary>
public sealed class ShiftEntityValidationEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var arg in context.Arguments)
        {
            if (arg is not ShiftEntityViewAndUpsertDTO dto)
                continue;

            var validationContext = new ValidationContext(dto);
            var results = new List<ValidationResult>();

            if (Validator.TryValidateObject(dto, validationContext, results, validateAllProperties: true))
                continue;

            // Group by member name, matching the shape MVC's ModelState produces.
            var grouped = results
                .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : new[] { string.Empty })
                    .Select(m => new { Key = m, Error = r.ErrorMessage ?? string.Empty }))
                .GroupBy(x => x.Key)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Error).ToArray());

            var response = new ShiftEntityResponse<object>
            {
                Message = new Message
                {
                    Title = "Model Validation Error",
                    SubMessages = grouped.Select(x => new Message
                    {
                        Title = x.Key,
                        For = x.Key,
                        SubMessages = x.Value.Select(e => new Message { Title = e }).ToList()
                    }).ToList()
                }
            };

            return Results.Json(response, statusCode: 400);
        }

        return await next(context);
    }
}
