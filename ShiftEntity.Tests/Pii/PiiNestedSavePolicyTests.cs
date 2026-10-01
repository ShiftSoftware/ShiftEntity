using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web.Pii;
using ShiftSoftware.TypeAuth.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Pii;

public class PiiNestedSavePolicyTests
{
    [Fact]
    public void Masked_children_are_matched_by_id_after_reordering_without_pii_permission()
    {
        using var services = Services(false);
        var entity = Stored();
        var dto = new CustomerDTO { Phones = [Keep("22"), Keep("11")] };
        var policy = PiiSavePolicy.Prepare(dto, entity, services, false);
        entity.Phones.Reverse();
        foreach (var phone in entity.Phones)
            phone.Number = dto.Phones.Single(x => x.ID == phone.ID.ToString()).Number!.Value;
        policy.ValidateMapped(entity);
        Assert.Equal("second", entity.Phones[0].Number);
        Assert.Equal("first", entity.Phones[1].Number);
        Assert.All(dto.Phones, phone => Assert.Null(phone.Number!.Display));
    }

    [Fact]
    public void Nested_single_object_and_omitted_field_preserve_storage()
    {
        using var services = Services(false);
        var entity = new Customer { Details = new Phone { Number = "original" } };
        var dto = new CustomerDTO { Details = new PhoneDTO() };
        var policy = PiiSavePolicy.Prepare(dto, entity, services, false);
        Assert.Equal("original", dto.Details.Number!.Value);
        policy.ValidateMapped(entity);
    }

    [Fact]
    public void Authorized_add_remove_and_replace_validate_and_return_resolved_values()
    {
        using var services = Services(true);
        var entity = Stored();
        var dto = new CustomerDTO { Phones = [new() { ID = "22", Number = new() { Value = "edited", Write = "replace" } },
            new() { Number = new() { Value = "added", Write = "replace" } }] };
        var policy = PiiSavePolicy.Prepare(dto, entity, services, false);
        entity.Phones = [new() { ID = 22, Number = "edited" }, new() { Number = "added" }];
        policy.ValidateMapped(entity);
        Assert.Equal(2, entity.Phones.Count);
    }

    [Fact]
    public void Generated_maps_may_recreate_rows_but_cannot_swap_protected_values()
    {
        using var services = Services(false);
        var entity = Stored();
        var dto = new CustomerDTO { Phones = [Keep("22"), Keep("11")] };
        var policy = PiiSavePolicy.Prepare(dto, entity, services, false);
        entity.Phones = dto.Phones.Select(x => new Phone { Number = x.Number!.Value }).ToList();
        policy.ValidateMapped(entity);
        entity.Phones.Reverse();
        Assert.Throws<InvalidOperationException>(() => policy.ValidateMapped(entity));
    }

    [Theory]
    [InlineData("22", "22")]
    [InlineData("999", "11")]
    public void Duplicate_and_foreign_child_ids_are_rejected(string first, string second)
    {
        using var services = Services(true);
        var entity = Stored();
        var error = Assert.Throws<ShiftEntityException>(() => PiiSavePolicy.Prepare(
            new CustomerDTO { Phones = [Keep(first), Keep(second)] }, entity, services, false));
        Assert.Equal(400, error.HttpStatusCode);
        Assert.Equal("first", entity.Phones[0].Number);
    }

    [Fact]
    public void Nested_replacement_requires_pii_permission()
    {
        using var services = Services(false);
        var dto = new CustomerDTO { Phones = [new() { ID = "11", Number = new() { Value = "edited", Write = "replace" } }] };
        Assert.Equal(403, Assert.Throws<ShiftEntityException>(() =>
            PiiSavePolicy.Prepare(dto, Stored(), services, false)).HttpStatusCode);
    }

    [Fact]
    public void Required_validation_for_new_children_and_clears_uses_the_full_field_path()
    {
        using var services = Services(true);
        foreach (var item in new[] { new PhoneDTO(), new PhoneDTO { ID = "11", Number = new() { Value = null, Write = "replace" } } })
        {
            var error = Assert.Throws<ShiftEntityException>(() => PiiSavePolicy.Prepare(
                new CustomerDTO { Phones = [item] }, Stored(), services, false));
            Assert.Equal(400, error.HttpStatusCode);
            Assert.Contains(error.Message.SubMessages!, x => x.For == "Phones[0].Number");
        }
    }

    [Fact]
    public void Empty_and_null_subgraphs_follow_ordinary_removal_rules()
    {
        using var services = Services(false);
        var entity = Stored();
        var policy = PiiSavePolicy.Prepare(new CustomerDTO(), entity, services, false);
        entity.Phones.Clear();
        policy.ValidateMapped(entity);
    }

    [Fact]
    public void A_mapper_cannot_redirect_a_nested_singleton_to_a_different_child()
    {
        using var services = Services(false);
        var entity = new Customer { Details = new() { ID = 11, Number = "stored" } };
        var dto = new CustomerDTO { Details = Keep("11") };
        var policy = PiiSavePolicy.Prepare(dto, entity, services, false);
        entity.Details = new() { ID = 99, Number = "stored" };
        Assert.Throws<InvalidOperationException>(() => policy.ValidateMapped(entity));
    }

    [Fact]
    public void Collections_without_item_ids_are_refused_before_mapping()
    {
        using var services = Services(true);
        var dto = new UnkeyedCustomerDTO { Phones = [new() { Number = new() { Value = "raw", Write = "replace" } }] };
        var error = Assert.Throws<ShiftEntityException>(() => PiiSavePolicy.Prepare(dto, new Customer(), services, true));
        Assert.Equal(400, error.HttpStatusCode);
    }

    [Fact]
    public void Several_nested_levels_preserve_required_masked_fields_and_validate_full_paths()
    {
        using var services = Services(false);
        var entity = new Envelope { Customer = Stored() };
        var dto = new EnvelopeDTO { Customer = new() { ID = "7", Phones = [Keep("22")] } };
        var policy = PiiSavePolicy.Prepare(dto, entity, services, false);
        entity.Customer.Phones.RemoveAt(0);
        policy.ValidateMapped(entity);
        Assert.Equal("second", dto.Customer.Phones[0].Number!.Value);

        using var allowed = Services(true);
        dto.Customer.Phones[0].Number = new() { Value = "", Write = "replace" };
        var error = Assert.Throws<ShiftEntityException>(() =>
            PiiSavePolicy.Prepare(dto, new Envelope { Customer = Stored() }, allowed, false));
        Assert.Contains(error.Message.SubMessages!, x => x.For == "Customer.Phones[0].Number");
    }

    [Fact]
    public void Creating_a_parent_validates_children_already_initialized_by_its_constructor()
    {
        using var services = Services(false);
        var entity = new Customer { Details = new Phone() };
        var dto = new CustomerDTO { Details = new PhoneDTO() };
        var error = Assert.Throws<ShiftEntityException>(() => PiiSavePolicy.Prepare(dto, entity, services, true));
        Assert.Equal(400, error.HttpStatusCode);
        Assert.Contains(error.Message.SubMessages!, x => x.For == "Details.Number");
    }

    private static Customer Stored() => new() { Phones = [new() { ID = 11, Number = "first" }, new() { ID = 22, Number = "second" }] };
    private static PhoneDTO Keep(string id) => new() { ID = id, Number = new() { Display = "mask", Value = "tampered", Write = "keep" } };
    private static ServiceProvider Services(bool grant)
    {
        var auth = Substitute.For<ITypeAuthService>();
        auth.CanAccess(Arg.Any<ShiftSoftware.TypeAuth.Core.Actions.BooleanAction>()).Returns(grant);
        return new ServiceCollection().AddSingleton(auth)
            .AddSingleton<IOptions<PiiOptions>>(Options.Create(new PiiOptions())).BuildServiceProvider();
    }

    public class Envelope { public Customer Customer { get; set; } = new(); }
    public class EnvelopeDTO { public CustomerDTO Customer { get; set; } = new(); }
    public class UnkeyedCustomerDTO { public List<UnkeyedPhoneDTO> Phones { get; set; } = []; }
    public class UnkeyedPhoneDTO { [Pii(PiiKind.Phone)] public PiiFieldDTO? Number { get; set; } }
    // Children are matched by the framework IDs: ShiftEntityBase on entities, ShiftEntityDTOBase on DTOs.
    public class Customer : ShiftEntityBase
    {
        public Customer() => ID = 7;
        public Phone? Details { get; set; }
        public List<Phone> Phones { get; set; } = [];
    }
    public class Phone : ShiftEntityBase { public string? Number { get; set; } }
    public class CustomerDTO : ShiftEntityViewAndUpsertDTO
    {
        public override string? ID { get; set; }
        public PhoneDTO? Details { get; set; }
        public List<PhoneDTO> Phones { get; set; } = [];
    }
    public class PhoneDTO : ShiftEntityDTOBase
    {
        public override string? ID { get; set; }
        [Pii(PiiKind.Phone), Required(ErrorMessage = "Phone is required."), StringLength(10)]
        public PiiFieldDTO? Number { get; set; }
    }
}
