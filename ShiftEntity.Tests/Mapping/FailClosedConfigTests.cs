using Microsoft.CodeAnalysis;
using System.Linq.Expressions;
using ShiftSoftware.ShiftEntity.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Pins that fluent configuration the generator cannot BAKE now fails, instead of being dropped.
/// <para>
/// The generator decides customize-vs-convention at build time. When it could not read a registration it
/// returned null and baked the plain convention — so the call compiled, ran, and did nothing whatsoever. You
/// could write <c>map.Ignore(x => x.Secret)</c> and still have the member mapped. <c>Ignore</c> was decoration.
/// </para>
/// <para>
/// Two layers, because one cannot cover the other. What is statically visible is a build error
/// (<c>SHENGEN009</c>). Configuration from ANOTHER ASSEMBLY is invisible to any compilation-local analysis, so
/// the generated mapper carries the set it baked and <c>VerifyBaked</c> throws on first use if something else
/// turns up.
/// </para>
/// </summary>
public class FailClosedConfigTests
{
    private const string Unbakeable = "SHENGEN009";

    /// <summary>A selector that is not a plain property access cannot be resolved to a member name.</summary>
    [Fact]
    public void NonLiteralMemberSelector_IsABuildError()
    {
        var diagnostic = Assert.Single(Run("""
            map.ForView(SelectName(), (e, ctx) => "x");
            """, helper: """
                private static System.Linq.Expressions.Expression<System.Func<WidgetDTO, string>> SelectName() => d => d.Name;
                """));

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("not a plain property access", diagnostic.GetMessage());
    }

    /// <summary>A computed depth is not readable at build time, and depth is baked into the emitted code.</summary>
    [Fact]
    public void NonConstantMaxDepth_IsABuildError()
    {
        var diagnostic = Assert.Single(Run("""
            map.MaxDepth(Depth);
            """, helper: """
                private static int Depth => 3;
                """));

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("not a compile-time constant", diagnostic.GetMessage());
    }

    /// <summary>Ordinary configuration is readable and must stay silent.</summary>
    [Fact]
    public void PlainConfiguration_IsSilent() => Assert.Empty(Run("""
        map.ForView(d => d.Name, (e, ctx) => e.Name);
        map.MaxDepth(3);
        """));

    /// <summary>
    /// The framework's own builders forward between overloads with open generic receivers. Those are API
    /// definitions, not registrations — reporting them would break the framework's own build, which is exactly
    /// what happened the first time this diagnostic was switched on.
    /// </summary>
    [Fact]
    public void FrameworkForwardingPlumbing_IsSilent()
    {
        var run = MapperGeneratorHarness.Run("""
            using System;
            using System.Linq.Expressions;
            using ShiftSoftware.ShiftEntity.Core;

            namespace ShiftSoftware.ShiftEntity.Core.Pretend;

            public class Forwarder<TEntity, TListDTO, TViewDTO>
            {
                private readonly ShiftMapperBuilder<TEntity, TListDTO, TViewDTO> inner = new();

                public void Forward<TProp>(Expression<Func<TViewDTO, TProp>> member, Func<TEntity, TProp> value)
                    => this.inner.ForView(member, value);
            }
            """);

        Assert.Empty(run.OfId(Unbakeable));
    }

    /// <summary>
    /// The runtime backstop. Configuration applied from outside the compilation the generator saw is registered
    /// against a member it never baked — so the mapper throws on first use, naming the member, rather than
    /// silently ignoring it.
    /// </summary>
    [Fact]
    public void ConfigurationTheGeneratorNeverSaw_ThrowsAtFirstUse()
    {
        var sample = MapperGeneratorHarness.Load("""
            using System;
            using ShiftSoftware.ShiftEntity.Core;
            using ShiftSoftware.ShiftEntity.EFCore;
            using ShiftSoftware.ShiftEntity.Model.Dtos;

            namespace Sample;

            public class SampleDb : ShiftDbContext { }

            public class Widget : ShiftEntity<Widget>
            {
                public string Name { get; set; } = "";
            }

            public class WidgetDTO : ShiftEntityViewAndUpsertDTO
            {
                public override string? ID { get; set; }
                public string Name { get; set; } = "";
            }

            public class WidgetRepository : ShiftRepository<SampleDb, Widget, WidgetDTO, WidgetDTO>
            {
                public WidgetRepository(SampleDb db) : base(db) { }
            }
            """);

        var mapper = sample.Mapper("Generated_Widget_");

        // map => map.IgnoreView(d => d.Name), built HERE at runtime rather than written in the scaffold. That is
        // what makes it a faithful stand-in for another assembly: had it been written in the scaffold, the
        // generator would have read it and baked it, and nothing would be under test.
        var builderType = mapper.BuilderType;
        var dtoType = sample.Type("Sample.WidgetDTO");

        var dto = Expression.Parameter(dtoType, "d");
        var selector = Expression.Lambda(Expression.Property(dto, "Name"), dto);

        var builder = Expression.Parameter(builderType, "map");
        var ignoreView = builderType.GetMethod("IgnoreView")!.MakeGenericMethod(typeof(string));

        var configure = Expression.Lambda(
            typeof(Action<>).MakeGenericType(builderType),
            Expression.Call(builder, ignoreView, Expression.Quote(selector)),
            builder).Compile();

        var error = Assert.Throws<InvalidOperationException>(() => mapper.AddConfiguration(configure));

        Assert.Contains("Name", error.Message);
        Assert.Contains("did not see those registrations at build time", error.Message);
    }

    // ──────────────────────────────── harness ────────────────────────────────

    private static string Scaffold(string configure, string helper) => $$"""
        using System;
        using System.Collections.Generic;
        using ShiftSoftware.ShiftEntity.Core;
        using ShiftSoftware.ShiftEntity.EFCore;
        using ShiftSoftware.ShiftEntity.Model.Dtos;

        namespace Sample;

        public class SampleDb : ShiftDbContext { }

        public class Widget : ShiftEntity<Widget>
        {
            public string Name { get; set; } = "";
        }

        public class WidgetDTO : ShiftEntityViewAndUpsertDTO
        {
            public override string? ID { get; set; }
            public string Name { get; set; } = "";
        }

        [ShiftEntityMapper]
        public partial class WidgetMapper : IShiftEntityMapper<Widget, WidgetDTO, WidgetDTO>
        {
            {{helper}}

            partial void Configure(ShiftMapperBuilder<Widget, WidgetDTO, WidgetDTO> map)
            {
                {{configure}}
            }
        }

        public class WidgetRepository : ShiftRepository<SampleDb, Widget, WidgetDTO, WidgetDTO>
        {
            public WidgetRepository(SampleDb db) : base(db) { }
        }
        """;

    private static IEnumerable<Diagnostic> Run(string configure, string helper = "") =>
        MapperGeneratorHarness.Run(Scaffold(configure, helper)).OfId(Unbakeable);
}
