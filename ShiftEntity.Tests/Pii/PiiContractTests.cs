using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.TypeAuth.Core.Actions;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Pii;

public class PiiContractTests
{
    [Theory]
    [InlineData(PiiKind.Phone, "+964 750 000 0088", "•••• 0088")]
    [InlineData(PiiKind.Name, "Ada Example", "A E")]
    [InlineData(PiiKind.Email, "ada@example.test", "a••••@example.test")]
    [InlineData(PiiKind.Address, "42 Example Street", "••••")]
    [InlineData(PiiKind.Identifier, "123456789", "••••")]
    public void Default_masks_do_not_serialize_the_raw_value(PiiKind kind, string raw, string expected)
    {
        using var services = new ServiceCollection().AddShiftEntityPii().BuildServiceProvider();
        var display = services.GetRequiredService<IPiiMasker>().Mask(kind, raw);
        var json = JsonSerializer.Serialize(new PiiFieldDTO { Display = display, Value = null, Write = "keep" });

        Assert.Equal(expected, display);
        Assert.DoesNotContain(raw, json);
        Assert.Contains("\"Value\":null", json);
    }

    [Fact]
    public void Host_can_change_mask_settings_and_choose_its_own_action()
    {
        var appAction = new BooleanAction("App protected fields");
        using var services = new ServiceCollection()
            .AddShiftEntityPii(o => { o.Action = appAction; o.VisiblePhoneDigits = 2; })
            .BuildServiceProvider();

        Assert.Same(appAction, services.GetRequiredService<IOptions<PiiOptions>>().Value.Action);
        Assert.NotSame(PiiActionTree.Reveal, appAction);
        Assert.Same(PiiActionTree.PartialSearch, services.GetRequiredService<IOptions<PiiOptions>>().Value.PartialSearchAction);
        Assert.Equal("•••• 88", services.GetRequiredService<IPiiMasker>().Mask(PiiKind.Phone, "+964 750 000 0088"));
    }

    [Fact]
    public void Host_can_replace_the_masker_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPiiMasker, TestMasker>();
        services.AddShiftEntityPii();
        using var provider = services.BuildServiceProvider();

        Assert.Equal("CUSTOM", provider.GetRequiredService<IPiiMasker>().Mask(PiiKind.Name, "Ada Example"));
    }

    [Fact]
    public void Protection_uses_inherited_server_kind_and_never_carries_the_raw_value()
    {
        using var provider = new ServiceCollection().AddShiftEntityPii().BuildServiceProvider();
        var field = provider.GetRequiredService<PiiFieldProtection>()
            .Protect(typeof(DerivedContactDTO), nameof(DerivedContactDTO.Phone), "+964 750 000 0088");

        Assert.Equal("•••• 0088", field.Display);
        Assert.Null(field.Value);
        Assert.Equal("keep", field.Write);
        Assert.DoesNotContain("+964 750 000 0088", JsonSerializer.Serialize(field));
    }

    [Fact]
    public void Nearest_override_declaration_controls_mask_and_reveal_policy()
    {
        using var provider = new ServiceCollection().AddShiftEntityPii().BuildServiceProvider();
        var member = typeof(LeafContactDTO).GetProperty(nameof(LeafContactDTO.Phone))!;
        var declaration = PiiFieldProtection.FindDeclaration(member);
        var field = provider.GetRequiredService<PiiFieldProtection>()
            .Protect(typeof(LeafContactDTO), nameof(LeafContactDTO.Phone), "+964 750 000 0088");

        Assert.Equal(PiiKind.Identifier, declaration?.Kind);
        Assert.False(declaration?.Revealable);
        Assert.Equal("••••", field.Display);
        Assert.Null(field.Value);
    }

    [Fact]
    public void Unclassified_or_non_wrapper_members_cannot_be_protected_as_pii()
    {
        using var provider = new ServiceCollection().AddShiftEntityPii().BuildServiceProvider();
        var protection = provider.GetRequiredService<PiiFieldProtection>();

        Assert.Throws<ArgumentException>(() => protection.Protect(typeof(DerivedContactDTO), nameof(DerivedContactDTO.Alias), "raw"));
        Assert.Throws<ArgumentException>(() => protection.Protect(typeof(DerivedContactDTO), nameof(DerivedContactDTO.Unclassified), "raw"));
    }

    [Fact]
    public void Nested_collections_are_protected_and_repeated_protection_keeps_the_mask()
    {
        using var provider = new ServiceCollection().AddShiftEntityPii().BuildServiceProvider();
        var protector = provider.GetRequiredService<PiiDtoProtector>();
        var dto = new ContactEnvelope
        {
            Contacts = [new OnlyPhoneDTO
            {
                Phone = new PiiFieldDTO { Value = "+964 750 000 0088" }
            }]
        };

        protector.Protect(dto);
        protector.Protect(dto);

        Assert.Equal("•••• 0088", dto.Contacts[0].Phone?.Display);
        Assert.Null(dto.Contacts[0].Phone?.Value);
        Assert.DoesNotContain("+964 750 000 0088", JsonSerializer.Serialize(dto));
    }

    [Fact]
    public void Tampering_with_a_previously_protected_wrapper_is_scrubbed_again()
    {
        using var provider = new ServiceCollection().AddShiftEntityPii().BuildServiceProvider();
        var protector = provider.GetRequiredService<PiiDtoProtector>();
        var dto = new OnlyPhoneDTO { Phone = new PiiFieldDTO { Value = "initial" } };

        protector.Protect(dto);
        dto.Phone!.Display = "synthetic-raw";
        protector.Protect(dto);

        Assert.Null(dto.Phone.Value);
        Assert.Null(dto.Phone.Display);
    }

    private class BaseContactDTO
    {
        [Pii(PiiKind.Phone)]
        public virtual PiiFieldDTO? Phone { get; set; }
    }

    private sealed class ContactEnvelope
    {
        public List<OnlyPhoneDTO> Contacts { get; set; } = [];
    }

    private sealed class OnlyPhoneDTO : BaseContactDTO
    {
        public override PiiFieldDTO? Phone { get; set; }
    }

    private sealed class DerivedContactDTO : BaseContactDTO
    {
        public override PiiFieldDTO? Phone { get; set; }
        public string? Alias { get; set; }
        public PiiFieldDTO? Unclassified { get; set; }
    }

    private class MiddleContactDTO : BaseContactDTO
    {
        [Pii(PiiKind.Identifier, Revealable = false)]
        public override PiiFieldDTO? Phone { get; set; }
    }

    private sealed class LeafContactDTO : MiddleContactDTO
    {
        public override PiiFieldDTO? Phone { get; set; }
    }

    private sealed class TestMasker : IPiiMasker
    {
        public string? Mask(PiiKind kind, string? rawValue) => "CUSTOM";
    }
}
