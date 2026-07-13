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
        public int? Age { get; set; }
    }

    private static ShiftMapperBuilder<WidgetEntity, WidgetListDTO, WidgetViewDTO> Builder() => new();

    // ─── ResolveView / ResolveEntity / ResolveCopy: the runtime path the generated code calls ───

    [Fact]
    public void ResolveView_NoCustomization_RunsConvention()
    {
        var result = Builder().ResolveView<string?>(new WidgetEntity { Name = "N" }, default, "Name", (e, _) => e.Name);
        Assert.Equal("N", result);
    }

    [Fact]
    public void ResolveView_WithCustomization_RunsCustomization_AndDefersConvention()
    {
        var builder = Builder().ForView(d => d.Name, (e, _) => "custom");
        var conventionRan = false;

        var result = builder.ResolveView<string?>(new WidgetEntity { Name = "N" }, default, "Name",
            (e, _) => { conventionRan = true; return e.Name; });

        Assert.Equal("custom", result);
        Assert.False(conventionRan);   // guard-before-execute: the convention delegate is never invoked
    }

    [Fact]
    public void ResolveView_NullableValueType_CastMatches()
    {
        // Mirrors the generated case where the customization's TProp is the (nullable) DTO property type.
        var builder = Builder().ForView(d => d.Age, (e, _) => (int?)42);

        var result = builder.ResolveView<int?>(new WidgetEntity(), default, "Age", (e, _) => null);

        Assert.Equal(42, result);
    }

    [Fact]
    public void ResolveEntity_WithCustomization_Wins()
    {
        var builder = Builder().ForEntity(x => x.Name, (dto, _) => "from-dto");

        var result = builder.ResolveEntity<string>(new WidgetViewDTO(), default, "Name", (d, _) => "convention");

        Assert.Equal("from-dto", result);
    }

    [Fact]
    public void ResolveCopy_NoCustomization_RunsConvention()
    {
        var result = Builder().ResolveCopy<string>(new WidgetEntity { Name = "src" }, default, "Name", (s, _) => s.Name);
        Assert.Equal("src", result);
    }

    // ─── value-fallback overloads (customization-only members: base/audit/excluded — no convention) ───

    [Fact]
    public void ResolveView_ValueFallback_NoCustomization_KeepsCurrent()
    {
        // The generated base-member form: dto.ID = ResolveView(entity, sp, "ID", dto.ID);
        var result = Builder().ResolveView<string>(new WidgetEntity(), default, "ID", current: "kept");
        Assert.Equal("kept", result);
    }

    [Fact]
    public void ResolveView_ValueFallback_WithCustomization_Overrides()
    {
        var builder = Builder().ForView(d => d.Name, (e, _) => "custom");
        var result = builder.ResolveView<string?>(new WidgetEntity(), default, "Name", current: "kept");
        Assert.Equal("custom", result);
    }

    [Fact]
    public void ResolveCopy_ValueFallback_PreservesTargetValue_WhenNotCustomized()
    {
        // The generated excluded form for CopyEntity: target.ReloadAfterSave = ResolveCopy(source, sp, "X", target.X);
        // — so target's value is kept (never copied from source) unless explicitly customized.
        var result = Builder().ResolveCopy<bool>(new WidgetEntity(), default, "ReloadAfterSave", current: true);
        Assert.True(result);
    }

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
        var result = ((Func<WidgetEntity, MappingContext, string?>)value!)(new WidgetEntity(), default);

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
