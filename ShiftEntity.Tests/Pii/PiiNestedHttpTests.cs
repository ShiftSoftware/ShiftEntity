using System.Net;
using System.Net.Http.Json;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.TypeAuth.Core.Actions;
using NSubstitute;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Pii;

public class PiiNestedHttpTests
{
    [Theory]
    [InlineData("/pii-validation-controller")]
    [InlineData("/pii-validation-minimal")]
    public async Task Existing_masked_values_can_be_kept_when_the_put_body_omits_the_root_id(string route)
    {
        using var fixture = new PiiValidationHttpTests.Fixture();
        using var client = fixture.Server.CreateClient();
        using var saved = await client.PutAsJsonAsync(route + "/7", new PiiValidationHttpTests.ContactDTO
        { Label = "edited", Phone = new() { Write = "keep" } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("stored", fixture.Current.Phone);
    }


    [Theory]
    [InlineData("/pii-validation-controller")]
    [InlineData("/pii-validation-minimal")]
    public async Task Nested_masked_round_trip_validation_retry_and_authorization_work(string route)
    {
        using var fixture = new PiiValidationHttpTests.Fixture();
        fixture.Current.Phones = [new() { ID = 11, Number = "first" }, new() { ID = 22, Number = "second" }];
        using var client = fixture.Server.CreateClient();
        var dto = new PiiValidationHttpTests.ContactDTO
        {
            ID = "7", Label = "edited", Phone = new() { Write = "keep" },
            Phones = [new() { ID = "22", Number = new() { Display = "mask", Write = "keep" } },
                      new() { ID = "11", Number = new() { Display = "mask", Value = "tampered", Write = "keep" } }]
        };
        fixture.Auth.CanAccess(Arg.Any<BooleanAction>()).Returns(false);
        using var kept = await client.PutAsJsonAsync(route + "/7", dto);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal("second", fixture.Current.Phones[0].Number);
        var body = await kept.Content.ReadAsStringAsync();
        Assert.DoesNotContain("first", body);
        Assert.DoesNotContain("second", body);
        Assert.DoesNotContain("tampered", body);

        dto.Phones[0].Number = new() { Value = "edited", Write = "replace" };
        using var deniedSave = await client.PutAsJsonAsync(route + "/7", dto);
        Assert.Equal(HttpStatusCode.Forbidden, deniedSave.StatusCode);
        Assert.Equal("second", fixture.Current.Phones[0].Number);

        fixture.Auth.CanAccess(Arg.Any<BooleanAction>()).Returns(true);
        dto.Phones[0].Number = new() { Value = null, Write = "replace" };
        using var invalid = await client.PutAsJsonAsync(route + "/7", dto);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<ShiftEntityResponse<PiiValidationHttpTests.ContactDTO>>())!.Message!.SubMessages!;
        Assert.Contains(errors, x => x.For == "Phones[0].Number");
        Assert.Equal("second", fixture.Current.Phones[0].Number);
        dto.Phones[0].Number = new() { Value = "edited", Write = "replace" };
        dto.Phones.RemoveAt(1);
        dto.Phones.Add(new() { Number = new() { Value = "added", Write = "replace" } });
        using var retry = await client.PutAsJsonAsync(route + "/7", dto);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(new[] { "edited", "added" }, fixture.Current.Phones.Select(x => x.Number));
        Assert.DoesNotContain("edited", (await retry.Content.ReadFromJsonAsync<ShiftEntityResponse<PiiValidationHttpTests.ContactDTO>>())!.Entity!.Phones[0].Number!.Display);
    }
}
