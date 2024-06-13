using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using ShiftSoftware.ShiftEntity.Functions.Extensions;
using ShiftSoftware.ShiftEntity.Model;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;

namespace ShiftSoftware.ShiftEntity.Functions.ModelValidation;

internal class ModelValidationMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ModelValidatorOptions options;
    private readonly IServiceProvider services;

    public ModelValidationMiddleware(ModelValidatorOptions options, IServiceProvider services)
    {
        this.options = options;
        this.services = services;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var methodInfo = context.GetTargetFunctionMethod();
        var httpContext = context.GetHttpContext();
        var request = httpContext.Request;
        request.EnableBuffering();
        using StreamReader reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        var requestBody = await reader.ReadToEndAsync();
        request.Body.Position = 0;

        ModelStateDictionary result = new();


        var parameters = context.FunctionDefinition.Parameters;

        foreach (var parameter in methodInfo.GetParameters())
        {
            var type = parameter.ParameterType;
            var attribute = parameter.GetCustomAttributes(false).OfType<Microsoft.Azure.Functions.Worker.Http.FromBodyAttribute>().FirstOrDefault();

            if (type.IsClass && !type.IsAbstract && !type.IsInterface && attribute is not null)
            {
                try
                {
                    //Get the request body
                    if (string.IsNullOrWhiteSpace(requestBody))
                        requestBody = "{}";
                    var value = JsonSerializer.Deserialize(requestBody!, parameter.ParameterType, 
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));

                    //Validate data annotations
                    var r = ModelValidator.Validate(value);
                    result.Merge(r);

                    //Validate FluentValidation
                    var validator = services.GetService(typeof(IValidator<>).MakeGenericType(parameter.ParameterType));
                    if (validator is not null)
                    {
                        var validationResult = await ((IValidator)validator).ValidateAsync(
                            new FluentValidation.ValidationContext<object>(value));
                        if (!validationResult.IsValid)
                            validationResult.AddToModelState(result);
                    }
                }
                catch (Exception)
                {
                    result.AddModelError("Invalid", "Invalid request body");
                }
            }
        }

        if (!result.IsValid)
        {
            if (!options.WrapValidationErrorResponseWithShiftEntityResponse)
            {
                var errorResponse = new SerializableError(result);
                var r = new BadRequestObjectResult(errorResponse);
                await r.ExecuteResultAsync(new ActionContext
                {
                    HttpContext = httpContext
                });
                return;
            }
            else
            {
                var errors = result.Select(x => new { x.Key, x.Value?.Errors }).ToDictionary(x => x.Key, x => x.Errors);

                var response = new ShiftEntityResponse<object>
                {
                    Additional = errors.ToDictionary(x => x.Key, x => (object)x.Value?.Select(s => s.ErrorMessage)!)
                };

                var r = new BadRequestObjectResult(response);
                await r.ExecuteResultAsync(new ActionContext
                {
                    HttpContext = httpContext
                });
                return;
            }
        }

        await next(context);
    }
}
