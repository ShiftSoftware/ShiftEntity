using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Phones;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.Validation;
using ShiftSoftware.ShiftEntity.Web.Pii;
using ShiftSoftware.TypeAuth.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Pii;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("07500000088", "IQ", "+964 750 000 0088")]
    [InlineData("0750-000-0088", "IQ", "+964 750 000 0088")]
    [InlineData("+964 (750) 000 0088", "IQ", "+964 750 000 0088")]
    [InlineData("009647500000088", "IQ", "+964 750 000 0088")]
    [InlineData("+1 (202) 555-0123", "IQ", "+1 202-555-0123")]
    [InlineData("020 7946 0018", "GB", "+44 20 7946 0018")]
    [InlineData("+44 20 7946 0018", null, "+44 20 7946 0018")]
    [InlineData("+9647500000088", null, "+964 750 000 0088")]
    [InlineData("+44 20 7946 0018", "IQ", "+44 20 7946 0018")]
    [InlineData("020 7946 0018", "gb", "+44 20 7946 0018")]
    [InlineData("2025550123", "US", "+1 202-555-0123")]
    [InlineData("02 3661 8300", "IT", "+39 02 3661 8300")]
    [InlineData("+39 02 3661 8300", null, "+39 02 3661 8300")]
    public void Complete_equivalents_use_host_format_and_explicit_foreign_country(string input, string? region, string expected)
    {
        var service = new PhoneNumberService(Options.Create(new PhoneNumberOptions { DefaultRegion = region }));
        Assert.True(service.TryNormalize(input, out var normalized, out var error), error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("07500000088", null)]
    [InlineData("+9647500000088", "invalid")]
    [InlineData("", "IQ")]
    [InlineData("123", "IQ")]
    [InlineData("not a phone", "IQ")]
    [InlineData("+9647500000088 ext 9", "IQ")]
    public void Invalid_or_ambiguous_input_returns_an_actionable_error_without_the_input(string input, string? region)
    {
        var service = new PhoneNumberService(Options.Create(new PhoneNumberOptions { DefaultRegion = region }));
        Assert.False(service.TryNormalize(input, out var normalized, out var error));
        Assert.Empty(normalized);
        Assert.NotEmpty(error);
        if (!string.IsNullOrEmpty(input)) Assert.DoesNotContain(input, error);
    }

    [Fact]
    public void E164_is_an_explicit_host_storage_choice()
    {
        var service = new PhoneNumberService(Options.Create(new PhoneNumberOptions { DefaultRegion = "IQ", StorageFormat = PhoneStorageFormat.E164 }));
        Assert.True(service.TryNormalize("0750-000-0088", out var normalized, out _));
        Assert.Equal("+9647500000088", normalized);
    }

    [Theory]
    [InlineData("07500000088")]
    [InlineData("020 7946 0018")]
    [InlineData("2025550123")]
    [InlineData("02 3661 8300")]
    public void Registration_without_configuration_never_assumes_a_country(string input)
    {
        using var services = new ServiceCollection().AddShiftPhoneNumbers().BuildServiceProvider();
        Assert.Null(services.GetRequiredService<IOptions<PhoneNumberOptions>>().Value.DefaultRegion);
        var service = services.GetRequiredService<IPhoneNumberService>();
        Assert.False(service.TryNormalize(input, out var normalized, out var error));
        Assert.Empty(normalized);
        Assert.Contains("configure a default phone region", error);
        Assert.True(service.TryNormalize("+1 202-555-0123", out normalized, out error), error);
        Assert.Equal("+1 202-555-0123", normalized);
    }

    [Fact]
    public void Wrapped_validation_never_mutates_and_save_normalizes_root_and_nested_replacements()
    {
        using var services = Services(true);
        var dto = new ContactDTO
        {
            Phone = new() { Value = "07500000088", Write = "replace" },
            Phones = [new() { Number = new() { Value = "+1 (202) 555-0123", Write = "replace" } }]
        };
        var wrapper = dto.Phone;
        Assert.True(WrappedValueValidator.TryValidateObject(dto, new ValidationContext(dto, services, null), new List<ValidationResult>()));
        Assert.Same(wrapper, dto.Phone);
        Assert.Equal("07500000088", dto.Phone.Value);
        var entity = new Contact();
        var policy = PiiSavePolicy.Prepare(dto, entity, services, true);
        Assert.Equal("+964 750 000 0088", dto.Phone.Value);
        Assert.Equal("+1 202-555-0123", dto.Phones[0].Number!.Value);
        entity.Phone = dto.Phone.Value;
        entity.Phones = [new() { Number = dto.Phones[0].Number!.Value }];
        policy.ValidateMapped(entity);
    }

    [Fact]
    public void Keep_preserves_legacy_storage_without_phone_validation_or_permission()
    {
        using var services = Services(false);
        var entity = new Contact { Phone = "legacy phone", Phones = [new() { ID = 5, Number = "legacy child" }] };
        var dto = new ContactDTO { Phone = new() { Value = "tampered", Write = "keep" }, Phones = [new() { ID = "5", Number = new() { Write = "keep" } }] };
        var policy = PiiSavePolicy.Prepare(dto, entity, services, false);
        policy.ValidateMapped(entity);
        Assert.Equal("legacy phone", dto.Phone.Value);
        Assert.Equal("legacy child", dto.Phones[0].Number!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0088")]
    [InlineData("invalid")]
    public void Invalid_nested_replacements_are_field_errors(string? input)
    {
        using var services = Services(true);
        var dto = new ContactDTO { Phone = new() { Write = "keep" }, Phones = [new() { Number = new() { Value = input, Write = "replace" } }] };
        var error = Assert.Throws<ShiftEntityException>(() => PiiSavePolicy.Prepare(dto, new Contact(), services, false));
        Assert.Equal(400, error.HttpStatusCode);
        Assert.Contains(error.Message.SubMessages!, e => e.For == "Phones[0].Number");
    }

    private static ServiceProvider Services(bool grant)
    {
        var auth = Substitute.For<ITypeAuthService>();
        auth.CanAccess(PiiActionTree.Reveal).Returns(grant);
        return new ServiceCollection().AddSingleton(auth).AddShiftEntityPii().AddShiftPhoneNumbers(o => o.DefaultRegion = "IQ").BuildServiceProvider();
    }
    public class Contact : ShiftEntity<Contact>
    {
        public string? Phone { get; set; }
        public List<Phone> Phones { get; set; } = [];
    }
    public class Phone : ShiftEntity<Phone> { public string? Number { get; set; } }
    public abstract class ContactFieldsDTO : ShiftEntityViewAndUpsertDTO
    {
        [Pii(PiiKind.Phone), Required, ValidPhoneNumber] public PiiFieldDTO? Phone { get; set; }
    }
    public class ContactDTO : ContactFieldsDTO
    {
        public override string? ID { get; set; }
        public List<PhoneDTO> Phones { get; set; } = [];
    }
    public class PhoneDTO : ShiftEntityDTOBase
    {
        public override string? ID { get; set; }
        [Pii(PiiKind.Phone), Required, ValidPhoneNumber] public PiiFieldDTO? Number { get; set; }
    }
}
