using ShiftSoftware.ShiftEntity.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pure unit tests for the DEEP-mapping builder sugar — <c>ForEntityChild(ren)</c> (replace-with-new via
/// the pair mapper's MapBack) and <c>ForListChild(ren)</c> (the pair's list projection composed into the
/// parent SQL projection). A fake pair mapper + projection is registered in
/// <see cref="ShiftEntityMapperRegistry"/> (what the generator does at module load) so these run without
/// the generator. Generated-mapper end-to-end deep composition is covered by the StockPlusPlus tests.
/// </summary>
public class ShiftMapperBuilderDeepTests
{
    public class ChildEntity
    {
        public long ID { get; set; }
        public string Name { get; set; } = "";
    }

    public class ChildDto
    {
        public string? ID { get; set; }
        public string? Name { get; set; }
    }

    public class ParentEntity
    {
        public long ID { get; set; }
        public List<ChildEntity> Children { get; set; } = new();
        public ChildEntity? Single { get; set; }
    }

    public class ParentListDTO
    {
        public string? ID { get; set; }
        public List<ChildDto>? Children { get; set; }
        public ChildDto? Single { get; set; }
    }

    public class ParentViewDTO
    {
        public List<ChildDto>? Children { get; set; }
        public ChildDto? Single { get; set; }
    }

    private sealed class FakeChildPairMapper : IShiftObjectMapper<ChildEntity, ChildDto>
    {
        public ChildDto Map(ChildEntity source, IServiceProvider? sp = null)
            => new() { ID = source.ID.ToString(), Name = source.Name };

        public ChildEntity MapBack(ChildDto dto, ChildEntity existing, IServiceProvider? sp = null)
        {
            existing.Name = dto.Name ?? "";
            return existing;
        }
    }

    static ShiftMapperBuilderDeepTests()
    {
        Expression<Func<ChildEntity, ChildDto>> projection = e => new ChildDto { ID = e.ID.ToString(), Name = e.Name };
        ShiftEntityMapperRegistry.RegisterPair(typeof(ChildEntity), typeof(ChildDto), typeof(FakeChildPairMapper), projection);
    }

    private static ShiftMapperBuilder<ParentEntity, ParentListDTO, ParentViewDTO> Builder() => new();

    // ─── ForEntityChildren / ForEntityChild — replace-with-new via the pair's MapBack ───

    [Fact]
    public void ForEntityChildren_MapsEachDtoToNewEntity_ViaPairMapBack()
    {
        var builder = Builder().ForEntityChildren(x => x.Children, d => d.Children);

        Assert.True(builder.TryGetEntityValue(nameof(ParentEntity.Children), out var value));
        var func = (Func<ParentViewDTO, IServiceProvider?, List<ChildEntity>?>)value!;

        var dto = new ParentViewDTO { Children = new() { new ChildDto { Name = "A" }, new ChildDto { Name = "B" } } };
        var result = func(dto, null);

        Assert.Equal(2, result!.Count);
        Assert.Equal("A", result[0].Name);
        Assert.Equal("B", result[1].Name);
        Assert.Equal(0, result[0].ID);   // new instance — DTO ID never written back
    }

    [Fact]
    public void ForEntityChildren_NullSource_YieldsNull()
    {
        var builder = Builder().ForEntityChildren(x => x.Children, d => d.Children);
        builder.TryGetEntityValue(nameof(ParentEntity.Children), out var value);

        var result = ((Func<ParentViewDTO, IServiceProvider?, List<ChildEntity>?>)value!)(new ParentViewDTO { Children = null }, null);

        Assert.Null(result);
    }

    [Fact]
    public void ForEntityChild_MapsSingleDtoToNewEntity()
    {
        var builder = Builder().ForEntityChild(x => x.Single, d => d.Single);
        builder.TryGetEntityValue(nameof(ParentEntity.Single), out var value);

        var result = ((Func<ParentViewDTO, IServiceProvider?, ChildEntity?>)value!)(
            new ParentViewDTO { Single = new ChildDto { Name = "S" } }, null);

        Assert.NotNull(result);
        Assert.Equal("S", result!.Name);
    }

    [Fact]
    public void ForEntityChildren_MissingPair_ThrowsClearError()
    {
        // A pair never registered.
        var builder = new ShiftMapperBuilder<UnregisteredEntity, UnregisteredEntity, UnregisteredEntity>();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            builder.ForEntityChildren(x => x.Kids, d => d.Kids));

        Assert.Contains("No source-generated pair mapper", ex.Message);
    }

    public class UnregisteredEntity
    {
        public List<UnregisteredChild> Kids { get; set; } = new();
    }

    public class UnregisteredChild { }

    // ─── ForListChildren / ForListChild — pair projection composed into the SQL projection ───

    [Fact]
    public void ForListChildren_ComposesChildProjection_IntoParentProjection()
    {
        Expression<Func<ParentEntity, ParentListDTO>> projection = e => new ParentListDTO { ID = e.ID.ToString() };

        var builder = Builder().ForListChildren<ChildEntity, ChildDto>(d => d.Children, e => e.Children);

        var composed = builder.ComposeList(projection);

        var parent = new ParentEntity
        {
            ID = 3,
            Children = new() { new ChildEntity { ID = 10, Name = "X" }, new ChildEntity { ID = 11, Name = "Y" } },
        };
        var result = new[] { parent }.AsQueryable().Select(composed).Single();

        Assert.Equal("3", result.ID);                 // parent convention binding kept
        Assert.Equal(2, result.Children!.Count);      // child collection projected
        Assert.Equal("10", result.Children[0].ID);
        Assert.Equal("X", result.Children[0].Name);
        Assert.Equal("Y", result.Children[1].Name);
    }

    [Fact]
    public void ForListChild_ComposesSingleChild_NullSafe()
    {
        Expression<Func<ParentEntity, ParentListDTO>> projection = e => new ParentListDTO { ID = e.ID.ToString() };

        var builder = Builder().ForListChild<ChildEntity, ChildDto>(d => d.Single, e => e.Single);
        var composed = builder.ComposeList(projection);

        var withChild = new ParentEntity { ID = 1, Single = new ChildEntity { ID = 5, Name = "Z" } };
        var withNull = new ParentEntity { ID = 2, Single = null };

        var results = new[] { withChild, withNull }.AsQueryable().Select(composed).ToList();

        Assert.Equal("Z", results[0].Single!.Name);
        Assert.Equal("5", results[0].Single!.ID);
        Assert.Null(results[1].Single);               // null-safe
    }

    [Fact]
    public void ForListChildren_ProducesMemberInit_SoODataStillWorks()
    {
        Expression<Func<ParentEntity, ParentListDTO>> projection = e => new ParentListDTO { ID = e.ID.ToString() };

        var composed = Builder().ForListChildren<ChildEntity, ChildDto>(d => d.Children, e => e.Children).ComposeList(projection);

        Assert.IsType<MemberInitExpression>(composed.Body);
    }
}
