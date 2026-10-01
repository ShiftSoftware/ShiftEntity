using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.WebUtilities;
using NSubstitute;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Print;
using ShiftSoftware.ShiftEntity.Web;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Print;

public class PrintTokenConfigurationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    public async Task Missing_key_allows_registration_but_returns_clear_server_error_on_print(string? key, bool useBuilder)
    {
        var services = new ServiceCollection();
        if (useBuilder)
            services.AddShiftEntityPrint(options => options.SASTokenKey = key!);
        else
            services.AddShiftEntityPrint(new ShiftEntityPrintOptions { SASTokenKey = key! });

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        var context = new DefaultHttpContext { RequestServices = provider };
        var handler = CreateHandler();

        var result = await handler.PrintTokenAsync(context, "document-7", "/print-token/document-7");

        Assert.Equal(500, result.StatusCode);
        var body = Assert.IsType<ShiftEntityResponse>(result.Body);
        Assert.Equal("Print configuration error", body.Message?.Title);
        Assert.Equal("ShiftEntityPrintOptions.SASTokenKey is required", body.Message?.Body);
    }

    [Fact]
    public async Task Configured_key_still_generates_a_token_bound_to_the_document_and_route()
    {
        using var scenario = new Scenario(visible: true);

        var result = scenario.Handler.PrintTokenAsync(scenario.Context, "document-7", "/print-token/document-7");
        var response = await result;

        Assert.Equal(200, response.StatusCode);
        var query = QueryHelpers.ParseQuery(Assert.IsType<string>(response.Body));
        Assert.True(scenario.Handler.ValidatePrintSASToken(scenario.Context, "document-7", "/print-token/document-7",
            query["expires"], query["token"]));
        Assert.False(scenario.Handler.ValidatePrintSASToken(scenario.Context, "document-8", "/print-token/document-7",
            query["expires"], query["token"]));
        Assert.False(scenario.Handler.ValidatePrintSASToken(scenario.Context, "document-7", "/other/print-token/document-7",
            query["expires"], query["token"]));
    }

    [Fact]
    public async Task Configured_key_keeps_the_not_found_response_for_an_inaccessible_document()
    {
        using var scenario = new Scenario(visible: false);

        var response = await scenario.Handler.PrintTokenAsync(scenario.Context, "document-7", "/print-token/document-7");

        Assert.Equal(404, response.StatusCode);
        var body = Assert.IsType<ShiftEntityResponse<DocumentDTO>>(response.Body);
        Assert.Equal("Not Found", body.Message?.Title);
    }

    private static ShiftEntityCrudHandler<IShiftRepositoryAsync<Document, DocumentListDTO, DocumentDTO>,
        Document, DocumentListDTO, DocumentDTO> CreateHandler() => new();

    private sealed class Scenario : IDisposable
    {
        private readonly ServiceProvider provider;
        public DefaultHttpContext Context { get; }
        public ShiftEntityCrudHandler<IShiftRepositoryAsync<Document, DocumentListDTO, DocumentDTO>,
            Document, DocumentListDTO, DocumentDTO> Handler { get; } = CreateHandler();

        public Scenario(bool visible)
        {
            var hashes = Substitute.For<IHashIdService>();
            hashes.Decode<DocumentDTO>("document-7").Returns(7);
            var repository = Substitute.For<IShiftRepositoryAsync<Document, DocumentListDTO, DocumentDTO>>();
            var found = Task.FromResult<Document?>(visible ? new Document() : null);
            repository.FindAsync(7, null, false, false).Returns(found);
            repository.FindAsync(7, null, RepositoryBypass.None).Returns(found);
            provider = new ServiceCollection()
                .AddSingleton(hashes)
                .AddSingleton(repository)
                .AddShiftEntityPrint(options => options.SASTokenKey = "synthetic-print-test-key")
                .BuildServiceProvider();
            Context = new DefaultHttpContext { RequestServices = provider };
        }

        public void Dispose() => provider.Dispose();
    }

    public sealed class Document : ShiftEntity<Document> { }

    public sealed class DocumentListDTO : ShiftEntityListDTO
    {
        public override string? ID { get; set; }
    }

    public sealed class DocumentDTO : ShiftEntityViewAndUpsertDTO
    {
        public override string? ID { get; set; }
    }
}
