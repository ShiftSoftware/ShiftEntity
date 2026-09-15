using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Web;
using ShiftSoftware.ShiftEntity.Web.Endpoints;
using System.Reflection;
using System.Text;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Endpoints;

public class PrintTokenResponseTests
{
    [Fact]
    public async Task MinimalApiPrintToken_IsPlainQueryText_NotAJsonString()
    {
        var result = Convert(CrudResult.Ok("expires=sample&token=sample"));
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var response = new MemoryStream();
        context.Response.Body = response;

        await result.ExecuteAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.StartsWith("text/plain", context.Response.ContentType);
        Assert.Equal("expires=sample&token=sample", Encoding.UTF8.GetString(response.ToArray()));
    }

    [Fact]
    public void MinimalApiStructuredErrors_StayJson()
    {
        var result = Convert(CrudResult.NotFound(new { Message = "Not Found" }));

        Assert.Contains("JsonHttpResult", result.GetType().Name);
    }

    private static IResult Convert(CrudResult result)
    {
        var method = typeof(ShiftEntityEndpointRouteBuilderExtensions)
            .GetMethod("ToMinimalApiResult", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (IResult)method.Invoke(null, [result])!;
    }
}
