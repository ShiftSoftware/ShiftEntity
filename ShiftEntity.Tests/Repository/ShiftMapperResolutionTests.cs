using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftMapper;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Mapping;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Tests.Auditing.Scenario;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Repository;

/// <summary>
/// The repository's door onto ShiftMapper — <c>docs/plans/repository-mapping-on-shiftmapper</c> (ShiftTemplates):
/// resolved when the host's <see cref="IMapper"/> declares all four maps of the triple (behind a DI-registered
/// <c>IShiftEntityMapper</c>, ahead of nothing — there is no other fallback), the <c>Mapping(...)</c> call recording
/// the nesting depth and handing the mapper NOTHING (a member customization is a mapper class's, not the
/// repository's), the action published around a write, and a value ShiftMapper cannot convert answered as a 400
/// naming the field rather than a 500.
/// <para>
/// Over a hand-written <see cref="IMapper"/> double: what is under test is the repository's resolution and the
/// adapter, not ShiftMapper's generated code, which the sample's end-to-end suites exercise.
/// </para>
/// </summary>
public class ShiftMapperResolutionTests
{
    /// <summary>An <see cref="IMapper"/> that declares the pairs it is given, records what it is configured with, and maps by delegate.</summary>
    private sealed class FakeMapper : IMapper
    {
        private readonly HashSet<(Type, Type)> pairs;

        public FakeMapper(params (Type Source, Type Destination)[] pairs) => this.pairs = pairs.ToHashSet();

        public List<ShiftMapperConfigurationSurface> Configured { get; } = new();

        public Func<object, object, object>? OnUpdate { get; set; }

        public bool CanMap(Type source, Type destination) => pairs.Contains((source, destination));

        public void Configure(ShiftMapperConfigurationSurface surface) => Configured.Add(surface);

        public TDestination Map<TDestination>(object source) => throw new NotSupportedException();

        public TDestination Map<TSource, TDestination>(TSource source) => throw new NotSupportedException();

        public TDestination Map<TSource, TDestination>(TSource source, TDestination destination) =>
            OnUpdate is null ? destination : (TDestination)OnUpdate(source!, destination!);

        public IQueryable<TDestination> ProjectTo<TSource, TDestination>(IQueryable<TSource> source) => throw new NotSupportedException();
    }

    /// <summary>The DTO of the triple these tests resolve — its own, so no other test class's host reaches it.</summary>
    public sealed class AutoOrderDTO : ShiftEntityDTOBase
    {
        public override string? ID { get; set; }
        public string Number { get; set; } = "";
    }

    private static (Type, Type)[] AllFour<TDto>() =>
    [
        (typeof(OrderEntity), typeof(TDto)),
        (typeof(TDto), typeof(OrderEntity)),
        (typeof(OrderEntity), typeof(TDto)),
        (typeof(OrderEntity), typeof(OrderEntity)),
    ];

    private static ServiceProvider Host(IMapper? mapper)
    {
        var services = new ServiceCollection();

        services.AddDbContext<OrderingDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<ICurrentUserProvider>(_ => FakeUserProvider.Anonymous());
        services.AddScoped<IdentityClaimProvider>();
        services.AddSingleton<IHashIdService>(new IdentityHashIdService());
        services.AddSingleton<IDefaultDataLevelAccess>(new RecordingDefaultDataLevelAccess());
        services.AddSingleton(new ShiftEntityOptions());
        services.AddScoped<ShiftEntityMappingContext>();
        services.AddScoped<IShiftEntityMappingContext>(sp => sp.GetRequiredService<ShiftEntityMappingContext>());

        if (mapper is not null)
            services.AddScoped(_ => mapper);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static ShiftRepository<OrderingDbContext, OrderEntity, AutoOrderDTO, AutoOrderDTO> Repo(
        IServiceScope scope,
        Action<ShiftRepositoryOptions<OrderEntity, AutoOrderDTO, AutoOrderDTO>>? configure = null)
        => new(scope.ServiceProvider.GetRequiredService<OrderingDbContext>(), configure);

    [Fact]
    public void AMapperCoveringAllFourMaps_IsResolvedBehindTheAdapter()
    {
        var mapper = new FakeMapper(AllFour<AutoOrderDTO>());

        using var provider = Host(mapper);
        using var scope = provider.CreateScope();

        var resolved = Assert.IsType<ShiftMapperEntityMapper<OrderEntity, AutoOrderDTO, AutoOrderDTO>>(Repo(scope).ShiftRepositoryOptions.Mapper);
        Assert.Same(mapper, resolved.Mapper);
    }

    /// <summary>All four or nothing: a triple half covered would be a first-request failure, not a startup one.</summary>
    [Fact]
    public void AMapperMissingOneMap_IsNotResolved()
    {
        var mapper = new FakeMapper(AllFour<AutoOrderDTO>().Take(3).ToArray());

        using var provider = Host(mapper);
        using var scope = provider.CreateScope();

        Assert.Null(Repo(scope).ShiftRepositoryOptions.Mapper);
    }

    /// <summary>
    /// A triple nothing covers resolves NO mapper, and the mapping methods throw rather than mapping by
    /// convention — the case startup validation exists to catch before any request reaches it.
    /// </summary>
    [Fact]
    public void NoMapperRegistered_ResolvesNothing_AndThrowsOnUse()
    {
        using var provider = Host(mapper: null);
        using var scope = provider.CreateScope();

        var repo = Repo(scope);
        Assert.Null(repo.ShiftRepositoryOptions.Mapper);

        var ex = Assert.Throws<InvalidOperationException>(() => repo.MapToView(new OrderEntity()));
        Assert.Contains("No mapper configured", ex.Message);
    }

    /// <summary>An explicitly configured mapper still wins — ShiftMapper only supplies the DEFAULT.</summary>
    [Fact]
    public void AnExplicitMapper_StillBeatsShiftMapper()
    {
        var mapper = new FakeMapper(AllFour<AutoOrderDTO>());

        using var provider = Host(mapper);
        using var scope = provider.CreateScope();

        var explicitMapper = new ExplicitOrderMapper();
        var repo = Repo(scope, o => o.UseMapper(explicitMapper));

        Assert.Same(explicitMapper, repo.ShiftRepositoryOptions.Mapper);
    }

    /// <summary>
    /// <c>Mapping(...)</c> is the repository's ONE word about its maps — how deep they nest — and the generator
    /// reads that at build time. At run time the call records the depth on the options and hands the mapper
    /// nothing: there is no per-repository configuration to apply, because what a member maps from is written
    /// in a mapper class, never in the repository.
    /// </summary>
    [Fact]
    public void Mapping_RecordsTheNestedDepth_AndHandsTheMapperNothing()
    {
        var mapper = new FakeMapper(AllFour<AutoOrderDTO>());

        using var provider = Host(mapper);
        using var scope = provider.CreateScope();

        var repo = Repo(scope, o => o.Mapping(m => m.Nested(2)));

        Assert.Equal(2, repo.ShiftRepositoryOptions.NestedMappingDepth);
        Assert.Empty(mapper.Configured);
    }

    [Fact]
    public void Mapping_WithoutNested_LeavesTheDefaultDepth()
    {
        var mapper = new FakeMapper(AllFour<AutoOrderDTO>());
        var ran = false;

        using var provider = Host(mapper);
        using var scope = provider.CreateScope();

        var repo = Repo(scope, o => o.Mapping(_ => ran = true));

        Assert.True(ran);
        Assert.Null(repo.ShiftRepositoryOptions.NestedMappingDepth);
    }

    [Fact]
    public void TwoMappingCalls_BothRun_TheLastDepthWins()
    {
        var mapper = new FakeMapper(AllFour<AutoOrderDTO>());
        var order = new List<string>();

        using var provider = Host(mapper);
        using var scope = provider.CreateScope();

        var repo = Repo(scope, o =>
        {
            o.Mapping(m => { order.Add("first"); m.Nested(3); });
            o.Mapping(m => { order.Add("second"); m.Nested(1); });
        });

        Assert.Equal(new[] { "first", "second" }, order);
        Assert.Equal(1, repo.ShiftRepositoryOptions.NestedMappingDepth);
    }

    /// <summary>
    /// A ShiftMapper map has no context parameter; the action it runs under is published on the scoped
    /// <see cref="IShiftEntityMappingContext"/> for exactly the duration of the write, and put back after.
    /// </summary>
    [Fact]
    public void TheActionType_IsPublishedAroundTheWrite_AndRestoredAfter()
    {
        var mapper = new FakeMapper(AllFour<AutoOrderDTO>());

        using var provider = Host(mapper);
        using var scope = provider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<IShiftEntityMappingContext>();
        ActionTypes? seen = null;

        mapper.OnUpdate = (_, destination) =>
        {
            seen = context.ActionType;
            return destination;
        };

        var repo = Repo(scope);
        Assert.Null(context.ActionType);

        repo.MapToEntity(new AutoOrderDTO(), new OrderEntity(), new MappingContext(scope.ServiceProvider, ActionTypes.Insert));

        Assert.Equal(ActionTypes.Insert, seen);
        Assert.Null(context.ActionType);
    }

    /// <summary>
    /// Text ShiftMapper cannot convert is the CLIENT's mistake: a 400 in the same "Model Validation Error" shape
    /// <c>MappingHelpers.ToForeignKey</c> produces, naming the DTO member the client sent — never the 500 an
    /// uncaught FormatException would be.
    /// </summary>
    [Fact]
    public void AValueShiftMapperCannotConvert_IsA400NamingTheField()
    {
        var mapper = new FakeMapper(AllFour<AutoOrderDTO>())
        {
            OnUpdate = (_, _) => throw Capture(() => ValueConverter.Parse<int>("abc", "AutoOrderDTO.Number -> OrderEntity.Number")),
        };

        using var provider = Host(mapper);
        using var scope = provider.CreateScope();

        var ex = Assert.Throws<ShiftEntityException>(() =>
            Repo(scope).MapToEntity(new AutoOrderDTO { Number = "abc" }, new OrderEntity(), new MappingContext(scope.ServiceProvider, ActionTypes.Update)));

        Assert.Equal(400, ex.HttpStatusCode);
        Assert.Equal("Model Validation Error", ex.Message.Title);
        Assert.Equal("Number", ex.Message.For);
        Assert.Contains("Number", ex.Message.Body);
    }

    /// <summary>
    /// The pack's <c>string → long</c>: blank is a 400 naming the field — the SOURCE member of the mapping, which
    /// for a select is the select itself — not the 0 ShiftMapper's own parser would write into a foreign key.
    /// </summary>
    [Fact]
    public void ABlankRequiredKey_IsA400NamingTheSelect()
    {
        var ex = Assert.Throws<ShiftEntityException>(() =>
            ShiftEntityConversions.ToRequiredLong("", "ProductDTO.Brand.Value -> Product.BrandID"));

        Assert.Equal(400, ex.HttpStatusCode);
        Assert.Equal("Brand", ex.Message.For);
        Assert.Equal("'Brand' is required.", ex.Message.Body);
    }

    [Fact]
    public void ANonNumericKey_IsA400NamingTheSelect()
    {
        var ex = Assert.Throws<ShiftEntityException>(() =>
            ShiftEntityConversions.ToRequiredLong("x", "ProductDTO.Brand.Value -> Product.BrandID"));

        Assert.Equal("Brand", ex.Message.For);
        Assert.Equal("'Brand' is not a valid selection.", ex.Message.Body);
    }

    [Fact]
    public void ANumericKey_Parses()
    {
        Assert.Equal(42L, ShiftEntityConversions.ToRequiredLong("42", "ProductDTO.Brand.Value -> Product.BrandID"));
        Assert.Equal("Number", ShiftEntityConversions.SourceMemberOf("OrderListDTO.Number -> OrderEntity.Number"));
    }

    private static Exception Capture(Action action)
    {
        try { action(); }
        catch (Exception e) { return e; }

        throw new InvalidOperationException("expected a throw");
    }

    /// <summary>A mapper plugged in with <c>UseMapper</c>: unmistakable, and never what ShiftMapper would resolve.</summary>
    private sealed class ExplicitOrderMapper : IShiftEntityMapper<OrderEntity, AutoOrderDTO, AutoOrderDTO>
    {
        public OrderEntity MapToEntity(AutoOrderDTO dto, OrderEntity existing, MappingContext context = default) => existing;
        public AutoOrderDTO MapToView(OrderEntity entity, MappingContext context = default) => throw new NotSupportedException();
        public IQueryable<AutoOrderDTO> MapToList(IQueryable<OrderEntity> query, MappingContext context = default) => throw new NotSupportedException();
        public void CopyEntity(OrderEntity source, OrderEntity target, MappingContext context = default) => throw new NotSupportedException();
    }
}
