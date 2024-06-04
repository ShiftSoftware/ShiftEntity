using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using ShiftSoftware.ShiftEntity.Functions.Extensions;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;

namespace ShiftSoftware.ShiftEntity.Functions.ModelValidation;

internal class ModelValidationMiddleware : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var methodInfo = context.GetTargetFunctionMethod();
        var httpContext = context.GetHttpContext();
        var request = httpContext.Request;
        request.EnableBuffering();
        using StreamReader reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        var requestBody = await reader.ReadToEndAsync();
        request.Body.Position = 0;

        ModelValidationResult result = new() { IsValid = true };

        var parameters = context.FunctionDefinition.Parameters;

        foreach (var parameter in methodInfo.GetParameters())
        {
            var type = parameter.ParameterType;
            var attribute = parameter.GetCustomAttributes(false).OfType<Microsoft.Azure.Functions.Worker.Http.FromBodyAttribute>().FirstOrDefault();

            if (type.IsClass && !type.IsAbstract && !type.IsInterface && attribute is not null)
            {
                try
                {
                    if(string.IsNullOrWhiteSpace(requestBody))
                        requestBody= "{}";

                    var value = JsonSerializer.Deserialize(requestBody!, parameter.ParameterType);
                
                    result = ModelValidator.Validate(value);
                }
                catch (Exception)
                {

                    throw;
                }
            }
        }

        if (!result.IsValid)
        {
            var errors = result.Results;
            var errorResponse = new
            {
                errors
            };

            var r = new BadRequestObjectResult(errorResponse);
            await r.ExecuteResultAsync(new ActionContext
            {
                HttpContext = httpContext
            });
            return;
        }

        await next(context);
    }
}
