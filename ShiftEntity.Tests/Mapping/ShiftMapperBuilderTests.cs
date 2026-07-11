using ShiftSoftware.ShiftEntity.Core;
using System;
using System.Linq;
using System.Linq.Expressions;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pure unit tests for <see cref="ShiftMapperBuilder{TEntity, TListDTO, TViewDTO}"/> — the registration
/// surface (typed selectors, last-wins) and the list projection composer (binding replacement/addition,
/// member-init preservation, parameter substitution). Generated-mapper end-to-end behavior is covered by
/// the StockPlusPlus sample tests.
/// </summary>
public class ShiftMapperBuilderTests
{
    private class WidgetEntity
    {
        public long ID { get; set; }
        public string Name { get; set; } = "";
        public string? Extra { get; set; }
    }

    private class WidgetListDTO
    {
        public string? ID { get; set; }
        public string? Name { get; set; }
        public string? Extra { get; set; }
    }

    private class WidgetViewDTO
    {
        public string? Name { get; set; }
    }

    private static ShiftMapperBuilder<WidgetEntity, WidgetListDTO, WidgetViewDTO> Builder() => new();

    [Fact]
    public void MemberSelector_MustBeSimplePropertyAccess()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Builder().ForView(d => d.Name + "!", (e, _) => "x"));

        Assert.Contains("simple property access", ex.Message);
    }

    [Fact]
    public void LastRegistration_Wins()
    {
        var builder = Builder()
            .ForView(d => d.Name, (e, _) => "first")
            .ForView(d => d.Name, (e, _) => "second");

        Assert.True(builder.TryGetViewValue(nameof(WidgetViewDTO.Name), out var value));
        var result = ((Func<WidgetEntity, IServiceProvider?, string?>)value!)(new WidgetEntity(), null);

        Assert.Equal("second", result);
    }

    [Fact]
    public void ForEntity_And_ForCopy_AreStoredIndependently()
    {
        var builder = Builder()
            .ForEntity(x => x.Name, (dto, _) => "from-dto")
            .ForCopy(x => x.Name, (source, _) => "from-source");

        Assert.True(builder.TryGetEntityValue(nameof(WidgetEntity.Name), out _));
        Assert.True(builder.TryGetCopyValue(nameof(WidgetEntity.Name), out _));
        Assert.False(builder.TryGetViewValue(nameof(WidgetEntity.Name), out _));
    }

    [Fact]
    public void ComposeList_ReplacesCustomizedBinding_AndKeepsOthers()
    {
        Expression<Func<WidgetEntity, WidgetListDTO>> projection =
            e => new WidgetListDTO { ID = e.ID.ToString(), Name = e.Name };

        var builder = Builder().ForList(d => d.Name, e => e.Name + " [L]");

        var composed = builder.ComposeList(projection);
        var result = new[] { new WidgetEntity { ID = 7, Name = "A" } }.AsQueryable().Select(composed).Single();

        Assert.Equal("7", result.ID);          // untouched convention binding kept
        Assert.Equal("A [L]", result.Name);    // customized binding replaced
    }

    [Fact]
    public void ComposeList_AddsBinding_ForMemberWithoutConvention()
    {
        Expression<Func<WidgetEntity, WidgetListDTO>> projection =
            e => new WidgetListDTO { Name = e.Name };

        var builder = Builder().ForList(d => d.Extra, e => e.Extra ?? "none");

        var composed = builder.ComposeList(projection);
        var result = new[] { new WidgetEntity { Name = "A", Extra = null } }.AsQueryable().Select(composed).Single();

        Assert.Equal("A", result.Name);
        Assert.Equal("none", result.Extra);
    }

    [Fact]
    public void ComposeList_WithoutCustomizations_ReturnsSameProjectionInstance()
    {
        Expression<Func<WidgetEntity, WidgetListDTO>> projection = e => new WidgetListDTO { Name = e.Name };

        Assert.Same(projection, Builder().ComposeList(projection));
    }

    [Fact]
    public void ComposeList_RequiresMemberInitProjection()
    {
        Expression<Func<WidgetEntity, WidgetListDTO>> projection = e => new WidgetListDTO();

        var builder = Builder().ForList(d => d.Name, e => e.Name);

        Assert.Throws<InvalidOperationException>(() => builder.ComposeList(projection));
    }

    [Fact]
    public void ComposeList_PreservesMemberInit_SoDownstreamValidatorsStillWork()
    {
        Expression<Func<WidgetEntity, WidgetListDTO>> projection = e => new WidgetListDTO { Name = e.Name };

        var composed = Builder().ForList(d => d.Extra, e => e.Extra).ComposeList(projection);

        Assert.IsType<MemberInitExpression>(composed.Body);
        Assert.Single(composed.Parameters);
    }
}
