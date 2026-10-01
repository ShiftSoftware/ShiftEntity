using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Pii;

public class PiiRevealHandlerTests
{
    [Fact]
    public async Task Authorized_reveal_returns_one_raw_field_with_no_store()
    {
        using var setup = new Scenario(grant: true, visible: true);
        var result = await setup.Handler.RevealPiiAsync(setup.Context, "contact-7", "Phone");

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("synthetic-phone-0088", Assert.IsType<PiiRevealDTO>(result.Body).Value);
        Assert.Equal("no-store", setup.Context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task Pii_grant_does_not_bypass_record_access()
    {
        using var setup = new Scenario(grant: true, visible: false);
        var result = await setup.Handler.RevealPiiAsync(setup.Context, "contact-7", "Phone");
        Assert.Equal(404, result.StatusCode);
    }

    [Fact]
    public async Task Read_access_without_pii_grant_cannot_reveal()
    {
        using var setup = new Scenario(grant: false, visible: true);
        var result = await setup.Handler.RevealPiiAsync(setup.Context, "contact-7", "Phone");
        Assert.Equal(403, result.StatusCode);
    }

    [Theory]
    [InlineData("Identifier")]
    [InlineData("Missing")]
    public async Task Non_revealable_or_unknown_field_is_denied(string member)
    {
        using var setup = new Scenario(grant: true, visible: true);
        var result = await setup.Handler.RevealPiiAsync(setup.Context, "contact-7", member);
        Assert.Equal(404, result.StatusCode);
    }

    private sealed class Scenario : IDisposable
    {
        private readonly ServiceProvider provider;
        public DefaultHttpContext Context { get; } = new();
        public ShiftEntityCrudHandler<IShiftRepositoryAsync<Contact, ContactListDTO, ContactDTO>,
            Contact, ContactListDTO, ContactDTO> Handler { get; } = new();

        public Scenario(bool grant, bool visible)
        {
            var action = new BooleanAction("App PII");
            var auth = Substitute.For<ITypeAuthService>();
            auth.CanAccess(action).Returns(grant);
            var hashes = Substitute.For<IHashIdService>();
            hashes.Decode<ContactDTO>("contact-7").Returns(7);
            var repo = Substitute.For<IShiftRepositoryAsync<Contact, ContactListDTO, ContactDTO>>();
            var found = Task.FromResult<Contact?>(visible
                ? new Contact { Phone = "synthetic-phone-0088", Identifier = "synthetic-id" }
                : null);
            repo.FindAsync(7, null, false, false).Returns(found);
            repo.FindAsync(7, null, RepositoryBypass.None).Returns(found);
            provider = new ServiceCollection()
                .AddSingleton(auth)
                .AddSingleton(hashes)
                .AddSingleton(repo)
                .AddSingleton<IOptions<PiiOptions>>(Options.Create(new PiiOptions { Action = action }))
                .BuildServiceProvider();
            Context.RequestServices = provider;
        }

        public void Dispose() => provider.Dispose();
    }

    public sealed class Contact : ShiftEntity<Contact>
    {
        public string? Phone { get; set; }
        public string? Identifier { get; set; }
    }

    public sealed class ContactDTO : ShiftEntityViewAndUpsertDTO
    {
        public override string? ID { get; set; }
        [Pii(PiiKind.Phone)] public PiiFieldDTO? Phone { get; set; }
        [Pii(PiiKind.Identifier, Revealable = false)] public PiiFieldDTO? Identifier { get; set; }
    }

    public sealed class ContactListDTO : ShiftEntityListDTO
    {
        public override string? ID { get; set; }
    }
}
