using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.Validation;
using ShiftSoftware.ShiftEntity.Web.Pii;
using ShiftSoftware.TypeAuth.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Pii;

public class PiiValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Required_create_or_clear_reports_the_field_and_preserves_storage(string? value)
    {
        using var services = Services();
        var entity = new Contact { Phone = "stored" };
        foreach (var isCreate in new[] { false, true })
        {
            var dto = new ContactDTO { Phone = new() { Value = value, Write = "replace" } };
            var error = Assert.Throws<ShiftEntityException>(() => PiiSavePolicy.Prepare(dto, entity, services, isCreate));
            Assert.Equal(400, error.HttpStatusCode);
            Assert.Equal("Phone", Assert.Single(error.Message.SubMessages!).For);
            Assert.Equal("Phone is required.", Assert.Single(error.Message.SubMessages![0].SubMessages!).Title);
            Assert.Equal("stored", entity.Phone);
        }
    }

    [Fact]
    public void Required_create_rejects_an_omitted_wrapper()
    {
        using var services = Services();
        var error = Assert.Throws<ShiftEntityException>(() => PiiSavePolicy.Prepare(new ContactDTO(), new Contact(), services, true));
        Assert.Equal("Phone", Assert.Single(error.Message.SubMessages!).For);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tampered")]
    public void Keep_does_not_validate_mask_or_submitted_value_or_require_pii_access(string? value)
    {
        using var services = Services(grant: false);
        var entity = new Contact { Phone = "stored" };
        var dto = new ContactDTO { Phone = new() { Display = "mask longer than ten", Value = value, Write = "keep" } };
        PiiSavePolicy.Prepare(dto, entity, services, false).ValidateMapped(entity);
        Assert.Equal("stored", dto.Phone.Value);
    }

    [Fact]
    public void Dto_and_entity_annotations_report_all_fields_without_echoing_raw_values()
    {
        using var services = Services();
        var entity = new Contact { Phone = "stored", Email = "stored@example.test" };
        var dto = new ContactDTO
        {
            Phone = new() { Value = "synthetic-too-long", Write = "replace" },
            Email = new() { Value = "synthetic-invalid-email", Write = "replace" }
        };
        var error = Assert.Throws<ShiftEntityException>(() => PiiSavePolicy.Prepare(dto, entity, services, false));
        Assert.Equal(new[] { "Phone", "Email" }, error.Message.SubMessages!.Select(x => x.For));
        var messages = string.Join(" ", error.Message.SubMessages.SelectMany(x => x.SubMessages!).Select(x => x.Title));
        Assert.DoesNotContain(dto.Phone.Value!, messages);
        Assert.DoesNotContain(dto.Email.Value!, messages);
        Assert.Equal("stored", entity.Phone);
    }

    [Fact]
    public void Wrapper_annotations_check_a_replacement_value_without_changing_the_wrapper()
    {
        var dto = new ContactDTO
        {
            Phone = new() { Value = "longer-than-ten", Write = "replace" },
            Email = new() { Value = "synthetic-invalid-email", Write = "replace" }
        };
        var results = new List<ValidationResult>();
        Assert.False(WrappedValueValidator.TryValidateObject(dto, new ValidationContext(dto), results));
        Assert.Equal(new[] { "Phone", "Email" }, results.Select(x => x.MemberNames.Single()));
        var messages = string.Join(" ", results.Select(x => x.ErrorMessage));
        Assert.DoesNotContain("longer-than-ten", messages);
        Assert.DoesNotContain("synthetic-invalid-email", messages);
        Assert.Equal("longer-than-ten", dto.Phone.Value);

        dto.Phone.Value = "valid";
        dto.Email.Value = "a@example.test";
        results.Clear();
        Assert.True(WrappedValueValidator.TryValidateObject(dto, new ValidationContext(dto), results));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void A_cleared_replacement_fails_the_required_rule(string? value)
    {
        var dto = new ContactDTO { Phone = new() { Value = value, Write = "replace" } };
        var results = new List<ValidationResult>();
        Assert.False(WrappedValueValidator.TryValidateObject(dto, new ValidationContext(dto), results));
        Assert.Equal("Phone is required.", Assert.Single(results).ErrorMessage);
    }

    [Fact]
    public void Keep_is_not_validated_and_a_missing_wrapper_is_validated_as_null()
    {
        var dto = new ContactDTO
        {
            Phone = new() { Display = "mask longer than ten", Value = "tampered value", Write = "keep" },
            Email = new() { Display = "not an email", Write = "keep" }
        };
        var results = new List<ValidationResult>();
        Assert.True(WrappedValueValidator.TryValidateObject(dto, new ValidationContext(dto), results));

        dto.Phone = null;
        Assert.False(WrappedValueValidator.TryValidateObject(dto, new ValidationContext(dto), results));
        Assert.Equal("Phone is required.", Assert.Single(results).ErrorMessage);
    }

    [Fact]
    public void A_single_wrapper_property_is_validated_like_the_whole_object()
    {
        var dto = new ContactDTO { Phone = new() { Value = "longer-than-ten", Write = "replace" } };
        var results = new List<ValidationResult>();
        var context = new ValidationContext(dto) { MemberName = nameof(ContactDTO.Phone) };
        Assert.False(WrappedValueValidator.TryValidateProperty(dto.Phone, context, results));
        Assert.Equal("Phone", Assert.Single(results).MemberNames.Single());

        dto.Phone.Write = "keep";
        results.Clear();
        Assert.True(WrappedValueValidator.TryValidateProperty(dto.Phone, context, results));
    }

    [Fact]
    public void Object_level_rules_run_in_the_validator_order_after_the_properties_pass()
    {
        var dto = new CheckedContactDTO { Phone = new() { Value = "longer-than-ten", Write = "replace" } };
        var results = new List<ValidationResult>();
        Assert.False(WrappedValueValidator.TryValidateObject(dto, new ValidationContext(dto), results));
        Assert.Equal("Phone", Assert.Single(results).MemberNames.Single());

        dto.Phone.Value = "valid";
        results.Clear();
        Assert.False(WrappedValueValidator.TryValidateObject(dto, new ValidationContext(dto), results));
        Assert.Equal("Type rule.", Assert.Single(results).ErrorMessage);

        dto.PassTypeRule = true;
        results.Clear();
        Assert.False(WrappedValueValidator.TryValidateObject(dto, new ValidationContext(dto), results));
        Assert.Equal("Object rule.", Assert.Single(results).ErrorMessage);
    }

    private static ServiceProvider Services(bool grant = true)
    {
        var options = new PiiOptions();
        var auth = Substitute.For<ITypeAuthService>();
        auth.CanAccess(options.Action).Returns(grant);
        return new ServiceCollection().AddSingleton(auth)
            .AddSingleton<IOptions<PiiOptions>>(Options.Create(options)).BuildServiceProvider();
    }

    private class ContactDTO
    {
        [Pii(PiiKind.Phone), Required(ErrorMessage = "Phone is required."), StringLength(10)]
        public PiiFieldDTO? Phone { get; set; }
        [Pii(PiiKind.Email), EmailAddress]
        public PiiFieldDTO? Email { get; set; }
    }

    private class Contact
    {
        public string? Phone { get; set; }
        [StringLength(20)] public string? Email { get; set; }
    }

    [TypeRule]
    private class CheckedContactDTO : ContactDTO, IValidatableObject
    {
        public bool PassTypeRule { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
            => [new ValidationResult("Object rule.")];
    }

    private sealed class TypeRuleAttribute : ValidationAttribute
    {
        public override bool IsValid(object? value) => value is CheckedContactDTO { PassTypeRule: true };
        public override string FormatErrorMessage(string name) => "Type rule.";
    }
}
