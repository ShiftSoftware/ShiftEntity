using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Abstractions;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Configurations;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace ShiftSoftware.ShiftEntity.Functions.Services
{
    public class OpenApiHttpTriggerAuthorization : IOpenApiHttpTriggerAuthorization
    {
        private readonly string apiKey;
        public OpenApiHttpTriggerAuthorization(IConfiguration configuration)
        {
            apiKey = configuration["Values:OpenApi__ApiKey"];

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new NullReferenceException("Values:OpenApi__ApiKey configuration is null or empty");
            }
        }
        public Task<OpenApiAuthorizationResult> AuthorizeAsync(IHttpRequestDataObject req)
        {
            var result = default(OpenApiAuthorizationResult);

            var queryFunctionKey = (string)req.Query["code"];
            var headerFunctionKey = (string)req.Headers["x-functions-key"];

            if ((string.IsNullOrWhiteSpace(queryFunctionKey) || queryFunctionKey.Equals(apiKey) == false) && (string.IsNullOrWhiteSpace(headerFunctionKey) || headerFunctionKey.Equals(apiKey) == false))
            {
                result = new OpenApiAuthorizationResult()
                {
                    StatusCode = HttpStatusCode.Unauthorized,
                    ContentType = "text/plain",
                    Payload = ""
                };

                return Task.FromResult(result);
            }

            return Task.FromResult(result);

        }
    }
}
