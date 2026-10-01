using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web;
using ShiftSoftware.ShiftEntity.Web.Endpoints;
using ShiftSoftware.TypeAuth.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Pii;

public class PiiValidationHttpTests
{
    [Theory]
    [InlineData("/pii-validation-controller")]
    [InlineData("/pii-validation-minimal")]
    public async Task Create_clear_length_email_keep_and_retry_work_through_the_real_http_pipeline(string route)
    {
        using var fixture = new Fixture();
        using var client = fixture.Server.CreateClient();
        using var missing = await client.PostAsJsonAsync(route, new ContactDTO { Label = "synthetic" });
        await AssertFieldError(missing, "Phone");
        Assert.Equal(0, fixture.Saves);

        foreach (var raw in new string?[] { null, "", " ", "synthetic-too-long" })
        {
            using var invalid = await client.PutAsJsonAsync(route + "/7", new ContactDTO
            {
                ID = "7", Label = "synthetic", Phone = new() { Value = raw, Write = "replace" }
            });
            await AssertFieldError(invalid, "Phone");
            Assert.Equal("stored", fixture.Current.Phone);
        }

        using var badEmail = await client.PutAsJsonAsync(route + "/7", new ContactDTO
        {
            ID = "7", Label = "synthetic", Phone = new() { Write = "keep" },
            Email = new() { Value = "synthetic-invalid-email", Write = "replace" }
        });
        await AssertFieldError(badEmail, "Email");
        Assert.Equal(0, fixture.Saves);

        fixture.Auth.CanAccess(Arg.Any<ShiftSoftware.TypeAuth.Core.Actions.BooleanAction>()).Returns(false);
        using var kept = await client.PutAsJsonAsync(route + "/7", new ContactDTO
        {
            ID = "7", Label = "edited", Phone = new() { Display = "very long mask", Value = "ignored", Write = "keep" }
        });
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal("stored", fixture.Current.Phone);
        Assert.DoesNotContain("stored", await kept.Content.ReadAsStringAsync());

        fixture.Auth.CanAccess(Arg.Any<ShiftSoftware.TypeAuth.Core.Actions.BooleanAction>()).Returns(true);
        using var retry = await client.PutAsJsonAsync(route + "/7", new ContactDTO
        {
            ID = "7", Label = "edited", Phone = new() { Value = "new-phone", Write = "replace" },
            Email = new() { Value = "a@example.test", Write = "replace" }
        });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("new-phone", fixture.Current.Phone);
        Assert.DoesNotContain("new-phone", await retry.Content.ReadAsStringAsync());
        Assert.Equal(2, fixture.Saves);

        using var created = await client.PostAsJsonAsync(route, new ContactDTO
        {
            Label = "created", Phone = new() { Value = "valid", Write = "replace" }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(3, fixture.Saves);
    }

    [Theory]
    [InlineData("/pii-validation-controller")]
    [InlineData("/pii-validation-minimal")]
    public async Task Ordinary_validation_and_pii_authorization_remain_effective(string route)
    {
        using var fixture = new Fixture();
        using var client = fixture.Server.CreateClient();
        using var noLabel = await client.PostAsJsonAsync(route, new ContactDTO
        {
            Phone = new() { Value = "valid", Write = "replace" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, noLabel.StatusCode);
        fixture.Auth.CanAccess(Arg.Any<ShiftSoftware.TypeAuth.Core.Actions.BooleanAction>()).Returns(false);
        using var forbidden = await client.PutAsJsonAsync(route + "/7", new ContactDTO
        {
            ID = "7", Label = "edited", Phone = new() { Value = "new-phone", Write = "replace" }
        });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("stored", fixture.Current.Phone);
        Assert.Equal(0, fixture.Saves);
    }

    [Fact]
    public async Task Other_controllers_check_raw_values_and_do_not_validate_keep()
    {
        using var fixture = new Fixture();
        using var client = fixture.Server.CreateClient();

        using var missing = await client.PostAsJsonAsync("/pii-validation-custom", new ContactDTO { Label = "synthetic" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("Phone is required.", await missing.Content.ReadAsStringAsync());

        // A length or format rule on a wrapper member used to throw or always fail. It now checks the raw value.
        using var invalid = await client.PostAsJsonAsync("/pii-validation-custom", new ContactDTO
        {
            Label = "synthetic", Phone = new() { Value = "synthetic-too-long", Write = "replace" },
            Email = new() { Value = "synthetic-invalid-email", Write = "replace" }
        });
        var invalidText = await invalid.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("The field Phone must be a string with a maximum length of 10.", invalidText);
        Assert.Contains("The Email field is not a valid e-mail address.", invalidText);
        Assert.DoesNotContain("synthetic-too-long", invalidText);
        Assert.DoesNotContain("synthetic-invalid-email", invalidText);

        using var kept = await client.PostAsJsonAsync("/pii-validation-custom", new ContactDTO
        {
            Label = "synthetic", Phone = new() { Display = "very long mask", Write = "keep" },
            Email = new() { Display = "a•••@example.test", Write = "keep" }
        });
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
    }

    private static async Task AssertFieldError(HttpResponseMessage response, string field)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
        var body = JsonSerializer.Deserialize<ShiftEntityResponse<ContactDTO>>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains(body!.Message!.SubMessages!, x => x.For == field && x.SubMessages!.Count > 0);
        Assert.DoesNotContain("synthetic-too-long", text);
        Assert.DoesNotContain("synthetic-invalid-email", text);
    }

    public sealed class Fixture : IDisposable
    {
        public TestServer Server { get; }
        public Contact Current { get; private set; } = new() { ID = 7, Label = "original", Phone = "stored", Email = "a@example.test" };
        public ITypeAuthService Auth { get; } = Substitute.For<ITypeAuthService>();
        public int Saves { get; private set; }

        public Fixture()
        {
            Auth.CanAccess(Arg.Any<ShiftSoftware.TypeAuth.Core.Actions.BooleanAction>()).Returns(true);
            var repository = Substitute.For<IShiftRepositoryAsync<Contact, ContactListDTO, ContactDTO>>();
            repository.FindAsync(7, null, false, false).Returns(_ => Task.FromResult<Contact?>(Current));
            repository.FindAsync(7, null, RepositoryBypass.None).Returns(_ => Task.FromResult<Contact?>(Current));
            repository.UpsertAsync(Arg.Any<Contact>(), Arg.Any<ContactDTO>(), Arg.Any<ActionTypes>(),
                Arg.Any<long?>(), Arg.Any<Guid?>(), Arg.Any<RepositoryBypass>()).Returns(call =>
            {
                var entity = call.Arg<Contact>();
                var dto = call.Arg<ContactDTO>();
                entity.ID = 7;
                entity.Label = dto.Label;
                entity.Phone = dto.Phone?.Value;
                entity.Email = dto.Email?.Value;
                Current = entity;
                return ValueTask.FromResult(entity);
            });
            repository.SaveChangesAsync().Returns(_ => Task.FromResult(++Saves));
            repository.ViewAsync(Arg.Any<Contact>()).Returns(call => ValueTask.FromResult(new ContactDTO
            {
                ID = "7", Label = call.Arg<Contact>().Label,
                Phone = new() { Value = call.Arg<Contact>().Phone },
                Email = new() { Value = call.Arg<Contact>().Email }
            }));

            Server = new TestServer(new WebHostBuilder().ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddControllers().AddApplicationPart(typeof(PiiValidationController).Assembly).AddShiftEntityWeb();
                services.AddShiftEntityPii();
                services.AddSingleton(Auth);
                services.AddSingleton(repository);
            }).Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                    endpoints.MapShiftEntityCrud<IShiftRepositoryAsync<Contact, ContactListDTO, ContactDTO>, Contact, ContactListDTO, ContactDTO>("/pii-validation-minimal");
                });
            }));
        }

        public void Dispose() => Server.Dispose();
    }

    public class Contact : ShiftEntity<Contact>
    {
        public string? Label { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
    }

    public class ContactListDTO : ShiftEntityListDTO
    {
        public override string? ID { get; set; }
    }

    public class ContactFieldsDTO : ShiftEntityViewAndUpsertDTO
    {
        public override string? ID { get; set; }
        [Required] public string? Label { get; set; }
        [Pii(PiiKind.Phone), Required(ErrorMessage = "Phone is required."), StringLength(10)]
        public PiiFieldDTO? Phone { get; set; }
        [Pii(PiiKind.Email), EmailAddress] public PiiFieldDTO? Email { get; set; }
    }

    public class ContactDTO : ContactFieldsDTO;
}

// Like the CRUD controllers in host apps, this is not an [ApiController]. Model state errors reach the shared
// handler, which returns them as field messages in the same shape as the minimal API route.
[Route("pii-validation-controller")]
public class PiiValidationController : ShiftEntityControllerAsync<
    IShiftRepositoryAsync<PiiValidationHttpTests.Contact, PiiValidationHttpTests.ContactListDTO, PiiValidationHttpTests.ContactDTO>,
    PiiValidationHttpTests.Contact, PiiValidationHttpTests.ContactListDTO, PiiValidationHttpTests.ContactDTO>;

[ApiController, Route("pii-validation-custom")]
public class PiiCustomValidationController : ControllerBase
{
    [HttpPost]
    public IActionResult Post(PiiValidationHttpTests.ContactDTO dto) => Ok();
}
