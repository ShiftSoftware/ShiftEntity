using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using NSubstitute;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web.Pii;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Pii;

public class PiiSavePolicyTests
{
    [Fact]
    public void Keep_ignores_the_submitted_raw_value_and_display()
    {
        using var services = Services(grant: false);
        var entity = new Contact { Phone = "stored" };
        var dto = new ContactDTO { Phone = new() { Display = "tampered", Value = "attacker", Write = "keep" } };

        var policy = PiiSavePolicy.Prepare(dto, entity, services, isCreate: false);
        entity.Phone = "stored";
        policy.ValidateMapped(entity);

        Assert.Equal("stored", entity.Phone);
        Assert.Equal("stored", dto.Phone?.Value);
        Assert.Null(dto.Phone?.Display);
    }

    [Fact]
    public void Replacement_needs_the_configured_action()
    {
        using var denied = Services(grant: false);
        var dto = new ContactDTO { Phone = new() { Value = "replacement", Write = "replace" } };

        var error = Assert.Throws<ShiftEntityException>(() =>
            PiiSavePolicy.Prepare(dto, new Contact { Phone = "stored" }, denied, isCreate: false));

        Assert.Equal(403, error.HttpStatusCode);
    }

    [Fact]
    public void Granted_replacement_and_clear_match_the_authorized_mapped_value()
    {
        using var allowed = Services(grant: true);
        var entity = new Contact { Phone = "stored" };
        var dto = new ContactDTO { Phone = new() { Value = "replacement", Write = "replace" } };

        var replacement = PiiSavePolicy.Prepare(dto, entity, allowed, isCreate: false);
        entity.Phone = dto.Phone?.Value;
        replacement.ValidateMapped(entity);
        Assert.Equal("replacement", entity.Phone);

        dto.Phone = new() { Value = null, Write = "replace" };
        var clear = PiiSavePolicy.Prepare(dto, entity, allowed, isCreate: false);
        entity.Phone = dto.Phone?.Value;
        clear.ValidateMapped(entity);
        Assert.Null(entity.Phone);
    }

    [Fact]
    public void Mapper_that_writes_a_different_protected_value_is_refused()
    {
        using var allowed = Services(grant: true);
        var entity = new Contact { Phone = "stored" };
        var dto = new ContactDTO { Phone = new() { Value = "replacement", Write = "replace" } };
        var policy = PiiSavePolicy.Prepare(dto, entity, allowed, isCreate: false);

        Assert.Throws<InvalidOperationException>(() => policy.ValidateMapped(entity));
    }

    [Fact]
    public void Replacement_runs_entity_validation_against_the_raw_value()
    {
        using var allowed = Services(grant: true);
        var dto = new ContactDTO { Phone = new() { Value = "too-long", Write = "replace" } };

        var error = Assert.Throws<ShiftEntityException>(() =>
            PiiSavePolicy.Prepare(dto, new ValidatedContact(), allowed, isCreate: false));

        Assert.Equal(400, error.HttpStatusCode);
    }

    [Fact]
    public void Nested_protected_updates_fail_closed()
    {
        using var allowed = Services(grant: true);
        var dto = new NestedContactDTO
        {
            Details = new ContactDTO { Phone = new() { Value = "synthetic", Write = "replace" } }
        };

        var error = Assert.Throws<ShiftEntityException>(() =>
            PiiSavePolicy.Prepare(dto, new Contact(), allowed, isCreate: false));

        Assert.Equal(400, error.HttpStatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    public void Missing_or_invalid_intent_is_rejected_without_writing(string? intent)
    {
        using var allowed = Services(grant: true);
        var entity = new Contact { Phone = "stored" };
        var dto = new ContactDTO { Phone = new() { Value = "attacker", Write = intent } };

        var error = Assert.Throws<ShiftEntityException>(() =>
            PiiSavePolicy.Prepare(dto, entity, allowed, isCreate: false));

        Assert.Equal(400, error.HttpStatusCode);
        Assert.Equal("stored", entity.Phone);
    }

    private static ServiceProvider Services(bool grant)
    {
        var action = new BooleanAction("App PII");
        var typeAuth = Substitute.For<ITypeAuthService>();
        typeAuth.CanAccess(action).Returns(grant);
        return new ServiceCollection()
            .AddSingleton(typeAuth)
            .AddSingleton<IOptions<PiiOptions>>(Options.Create(new PiiOptions { Action = action }))
            .BuildServiceProvider();
    }

    private sealed class Contact
    {
        public string? Phone { get; set; }
    }

    private sealed class ValidatedContact
    {
        [StringLength(5)]
        public string? Phone { get; set; }
    }

    private sealed class ContactDTO
    {
        [Pii(PiiKind.Phone)]
        public PiiFieldDTO? Phone { get; set; }
    }

    private sealed class NestedContactDTO
    {
        public ContactDTO? Details { get; set; }
    }
}
