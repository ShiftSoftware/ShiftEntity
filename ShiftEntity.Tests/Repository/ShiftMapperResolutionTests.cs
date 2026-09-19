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
/// The repository's door onto ShiftMapper — Stage 2 of <c>docs/plans/repository-mapping-on-shiftmapper</c>
/// (ShiftTemplates): resolved AHEAD of the old registry when the host's <see cref="IMapper"/> declares all four maps
/// of the triple (Stage 3 flipped the order), the <c>Mapping(...)</c> configuration handed to it on construction, the action published around a
/// write, and a value ShiftMapper cannot convert answered as a 400 naming the field rather than a 500.
/// <para>
/// Over a hand-written <see cref="IMapper"/> double: what is under test is the repository's resolution and the
/// adapter, not ShiftMapper's generated code, which the sample's parity harness diffs against the goldens.
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

    /// <summary>
    /// The DTO of the triple these tests resolve. ITS OWN: <see cref="ShiftEntityMapperRegistry"/> is process-wide
    /// static state, and a registration another test class makes for the shared <c>OrderListDTO</c> triple would
    /// answer here ahead of ShiftMapper.
    /// </summary>
    public sealed class AutoOrderDTO : ShiftEntityDTOBase
    {
        public override string? ID { get; set; }
        public string Number { get; set; } = "";
    }

    /// <summary>The triple the registry test registers for — separate, so the registration reaches no other test.</summary>
    public sealed class RegistryOrderDTO : ShiftEntityDTOBase
    {
        public override string? ID { get; set; }
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

    [Fact]
    public void NoMapperRegistered_ResolvesNothing()
    {
        using var provider = Host(mapper: null);
        using var scope = provider.CreateScope();

        Assert.Null(Repo(scope).ShiftRepositoryOptions.Mapper);
    }

    /// <summary>
    /// Stage 3 order: ShiftMapper is the default and the old generated mapper is the fallback — a triple the
    /// registry covers still resolves the registry's mapper when ShiftMapper declares nothing for it (a project
    /// mid-migration), and ShiftMapper's when it does.
    /// </summary>
    [Fact]
    public void ShiftMapper_WinsOverTheRegistry_WhichStaysTheFallback()
    {
        ShiftEntityMapperRegistry.Register(
            typeof(OrderEntity), typeof(RegistryOrderDTO), typeof(RegistryOrderDTO), typeof(RegistryOnlyMapper));

        using (var provider = Host(new FakeMapper(AllFour<RegistryOrderDTO>())))
        using (var scope = provider.CreateScope())
        {
            var repo = new ShiftRepository<OrderingDbContext, OrderEntity, RegistryOrderDTO, RegistryOrderDTO>(
                scope.ServiceProvider.GetRequiredService<OrderingDbContext>());

            Assert.IsType<ShiftMapperEntityMapper<OrderEntity, RegistryOrderDTO, RegistryOrderDTO>>(repo.ShiftRepositoryOptions.Mapper);
        }

        using (var provider = Host(new FakeMapper()))
        using (var scope = provider.CreateScope())
        {
            var repo = new ShiftRepository<OrderingDbContext, OrderEntity, RegistryOrderDTO, RegistryOrderDTO>(
                scope.ServiceProvider.GetRequiredService<OrderingDbContext>());

            Assert.IsType<RegistryOnlyMapper>(repo.ShiftRepositoryOptions.Mapper);
        }
    }

    /// <summary>
    /// The <c>Mapping(...)</c> lambda is run into a surface and handed to the mapper when the repository is
    /// constructed — whichever mapper ends up serving the repository — so a customized member's value is in the
    /// store before any service maps the pair.
    /// </summary>
    [Fact]
    public void TheMappingConfiguration_ReachesTheMapperOnConstruction()
    {
        var mapper = new FakeMapper(AllFour<AutoOrderDTO>());
        var ran = false;

        using var provider = Host(mapper);
        using var scope = provider.CreateScope();

        Repo(scope, o => o.Mapping(m => { ran = true; _ = m.List; }));

        Assert.True(ran);
        Assert.IsType<ShiftEntityMapping<OrderEntity, AutoOrderDTO, AutoOrderDTO>>(Assert.Single(mapper.Configured));
    }

    [Fact]
    public void TwoMappingCalls_BothRun_InOrder()
    {
        var mapper = new FakeMapper(AllFour<AutoOrderDTO>());
        var order = new List<string>();

        using var provider = Host(mapper);
        using var scope = provider.CreateScope();

        Repo(scope, o =>
        {
            o.Mapping(_ => order.Add("first"));
            o.Mapping(_ => order.Add("second"));
        });

        Assert.Equal(new[] { "first", "second" }, order);
        Assert.Single(mapper.Configured);
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

    private sealed class RegistryOnlyMapper : IShiftEntityMapper<OrderEntity, RegistryOrderDTO, RegistryOrderDTO>
    {
        public OrderEntity MapToEntity(RegistryOrderDTO dto, OrderEntity existing, MappingContext context = default) => existing;
        public RegistryOrderDTO MapToView(OrderEntity entity, MappingContext context = default) => throw new NotSupportedException();
        public IQueryable<RegistryOrderDTO> MapToList(IQueryable<OrderEntity> query, MappingContext context = default) => throw new NotSupportedException();
        public void CopyEntity(OrderEntity source, OrderEntity target, MappingContext context = default) => throw new NotSupportedException();
    }
}
