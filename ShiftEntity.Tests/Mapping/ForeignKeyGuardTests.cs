using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pins the FK guards in <see cref="MappingHelpers"/>. Both helpers used to be a bare <c>long.Parse</c>, so a
/// blank or non-numeric selection surfaced as a 500 with a stack trace and no indication of which field was at
/// fault. AutoMapper preserved the existing value on blank instead, which is a different wrong answer.
/// <para>
/// Most of the exposure is already covered upstream — on the MVC path, implicit-required-for-non-nullable
/// reference types plus ModelState reject a MISSING selection before mapping runs. Two residuals reach the
/// helper: a payload carrying a blank <c>{"Value":""}</c> (which passes validation), and minimal-API
/// endpoints, whose validation filter runs DataAnnotations only.
/// </para>
/// <para>
/// The required and nullable overloads differ deliberately: clearing an optional reference is legitimate, so
/// blank means <see langword="null"/> there and is an error only on the required one. Both reject a value that
/// is present but not a number, because that can only be a malformed request.
/// </para>
/// </summary>
public class ForeignKeyGuardTests
{
    private static ShiftEntitySelectDTO Select(string? value) => new() { Value = value! };

    // ── required ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToForeignKey_ParsesAValidSelection()
    {
        Assert.Equal(42L, Select("42").ToForeignKey());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToForeignKey_Throws400_WhenTheSelectionIsBlank(string? value)
    {
        var ex = Assert.Throws<ShiftEntityException>(() => Select(value).ToForeignKey());

        Assert.Equal(400, ex.HttpStatusCode);
    }

    [Fact]
    public void ToForeignKey_Throws400_WhenTheDtoItselfIsNull()
    {
        ShiftEntitySelectDTO? productBrand = null;

        var ex = Assert.Throws<ShiftEntityException>(() => productBrand!.ToForeignKey());

        Assert.Equal(400, ex.HttpStatusCode);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]   // long.MaxValue + 1 — overflow must not become a silent default
    public void ToForeignKey_Throws400_WhenTheValueIsNotALong(string value)
    {
        var ex = Assert.Throws<ShiftEntityException>(() => Select(value).ToForeignKey());

        Assert.Equal(400, ex.HttpStatusCode);
    }

    // The whole point of the guard is that the response names the offending field, so a form can bind an
    // inline error to it. CallerArgumentExpression supplies the argument text; the helper trims it to the
    // trailing member so the client sees "ProductBrand", not "dto.ProductBrand".
    [Fact]
    public void ToForeignKey_NamesTheOffendingMember()
    {
        var dto = new { ProductBrand = Select("") };

        var ex = Assert.Throws<ShiftEntityException>(() => dto.ProductBrand.ToForeignKey());

        Assert.Equal("ProductBrand", ex.Message.For);
        Assert.Contains("ProductBrand", ex.Message.Body);
    }

    // ── nullable ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToNullableForeignKey_ParsesAValidSelection()
    {
        Assert.Equal(7L, Select("7").ToNullableForeignKey());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToNullableForeignKey_ReturnsNull_OnBlank_RatherThanThrowing(string? value)
    {
        Assert.Null(Select(value).ToNullableForeignKey());
    }

    [Fact]
    public void ToNullableForeignKey_ReturnsNull_WhenTheDtoIsNull()
    {
        ShiftEntitySelectDTO? countryOfOrigin = null;

        Assert.Null(countryOfOrigin.ToNullableForeignKey());
    }

    // Blank clears; malformed is still a client error. Returning null here would silently drop a reference the
    // caller believed they were setting.
    [Fact]
    public void ToNullableForeignKey_Throws400_WhenTheValueIsPresentButNotALong()
    {
        var countryOfOrigin = Select("not-a-number");

        var ex = Assert.Throws<ShiftEntityException>(() => countryOfOrigin.ToNullableForeignKey());

        Assert.Equal(400, ex.HttpStatusCode);
        Assert.Equal("countryOfOrigin", ex.Message.For);
    }
}
