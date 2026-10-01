using NSubstitute;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Web.Pii;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Pii;

public class PiiNestedRevealPathTests
{
    [Fact]
    public void Collection_reveal_decodes_the_child_id_and_never_uses_its_position()
    {
        var hashes = Substitute.For<IHashIdService>();
        hashes.Decode("phone-key", typeof(PiiNestedSavePolicyTests.PhoneDTO)).Returns(22);
        var customer = new PiiNestedSavePolicyTests.Customer
        { Phones = [new() { ID = 11, Number = "first" }, new() { ID = 22, Number = "second" }] };
        var path = PiiMemberPath.Parse(typeof(PiiNestedSavePolicyTests.CustomerDTO), customer.GetType(), "Phones[phone-key].Number");
        Assert.NotNull(path);
        Assert.True(path.TryRead(customer, hashes, out var value));
        Assert.Equal("second", value);
        customer.Phones.Reverse();
        Assert.True(path.TryRead(customer, hashes, out value));
        Assert.Equal("second", value);
    }

    [Theory]
    [InlineData("Phones.Number")]
    [InlineData("Phones[].Number")]
    [InlineData("Phones[11].ID")]
    [InlineData("Phones[11][22].Number")]
    [InlineData("Missing.Number")]
    public void Invalid_or_unclassified_paths_are_refused(string field)
        => Assert.Null(PiiMemberPath.Parse(typeof(PiiNestedSavePolicyTests.CustomerDTO),
            typeof(PiiNestedSavePolicyTests.Customer), field));

    [Fact]
    public void A_nested_singleton_reveals_its_declared_field()
    {
        var customer = new PiiNestedSavePolicyTests.Customer { Details = new() { Number = "nested" } };
        var path = PiiMemberPath.Parse(typeof(PiiNestedSavePolicyTests.CustomerDTO), customer.GetType(), "Details.Number");
        Assert.True(path!.TryRead(customer, Substitute.For<IHashIdService>(), out var value));
        Assert.Equal("nested", value);
    }
}
