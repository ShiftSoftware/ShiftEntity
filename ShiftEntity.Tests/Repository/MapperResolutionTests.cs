using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Tests.Auditing.Scenario;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Repository;

/// <summary>
/// Which mapper a repository resolves when the builder configured none: an explicit DI registration first,
/// then the source-generated mapper from <see cref="ShiftEntityMapperRegistry"/>, then nothing.
/// <para>
/// The gap this closes (B-1) is that <c>ShiftRepository</c> never consulted the registry at all. A
/// source-generated mapper could exist, be correct, be registered, and the repository would still fall
/// through to AutoMapper and never know — which the Stage C parity inventory found live on one triple.
/// </para>
/// <para>
/// This used to be gated behind a <c>MappingMode</c> switch, so that wiring the registry in could not silently
/// swap a hand-tuned AutoMapper profile for convention output. With AutoMapper gone there is nothing to swap
/// FROM and nothing to opt into, so the mode went with it and the registry is consulted unconditionally.
/// </para>
/// </summary>
public class MapperResolutionTests
{
    /// <summary>Stands in for a source-generated mapper: parameterless, registry-resolvable, unmistakable.</summary>
    private sealed class RegistryOrderMapper : IShiftEntityMapper<OrderEntity, OrderListDTO, OrderListDTO>
    {
        public OrderEntity MapToEntity(OrderListDTO dto, OrderEntity existing, MappingContext context = default)
        {
            existing.Number = dto.Number;
            return existing;
        }

        public OrderListDTO MapToView(OrderEntity entity, MappingContext context = default) => throw new NotSupportedException();
        public IQueryable<OrderListDTO> MapToList(IQueryable<OrderEntity> query, MappingContext context = default) => throw new NotSupportedException();
        public void CopyEntity(OrderEntity source, OrderEntity target, MappingContext context = default) => throw new NotSupportedException();
    }

    /// <summary>A DTO nothing is ever registered for — the "no mapper covers this triple" case.</summary>
    private sealed class UnregisteredOrderDTO : ShiftEntityDTOBase
    {
        public override string? ID { get; set; }
    }

    private static ServiceProvider Host(bool withOptions = true)
    {
        var services = new ServiceCollection();

        services.AddDbContext<OrderingDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<ICurrentUserProvider>(_ => FakeUserProvider.Anonymous());
        services.AddScoped<IdentityClaimProvider>();
        services.AddSingleton<IHashIdService>(new IdentityHashIdService());
        services.AddSingleton<IDefaultDataLevelAccess>(new RecordingDefaultDataLevelAccess());

        if (withOptions)
            services.AddSingleton(new ShiftEntityOptions());

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static ShiftRepository<OrderingDbContext, OrderEntity, OrderListDTO, OrderListDTO> Repo(IServiceScope scope)
        => new(scope.ServiceProvider.GetRequiredService<OrderingDbContext>());

    private static void RegisterGeneratedMapper() =>
        ShiftEntityMapperRegistry.Register(
            typeof(OrderEntity), typeof(OrderListDTO), typeof(OrderListDTO), typeof(RegistryOrderMapper));

    [Fact]
    public void ResolvesTheRegistryMapper()
    {
        RegisterGeneratedMapper();

        using var provider = Host();
        using var scope = provider.CreateScope();

        Assert.IsType<RegistryOrderMapper>(Repo(scope).ShiftRepositoryOptions.Mapper);
    }

    /// <summary>
    /// A host that never configured <see cref="ShiftEntityOptions"/> resolves the registry just the same. The
    /// options object used to carry the mode that decided this; nothing about mapper resolution reads it any
    /// more, and a missing options registration must not quietly come to mean "no mapping".
    /// </summary>
    [Fact]
    public void ResolvesTheRegistryMapper_EvenWithoutOptionsConfigured()
    {
        RegisterGeneratedMapper();

        using var provider = Host(withOptions: false);
        using var scope = provider.CreateScope();

        Assert.IsType<RegistryOrderMapper>(Repo(scope).ShiftRepositoryOptions.Mapper);
    }

    /// <summary>
    /// A triple nothing covers resolves NO mapper, and the mapping methods throw rather than mapping by
    /// convention. This is the end state of the AutoMapper removal, and the case startup validation exists to
    /// catch before any request reaches it.
    /// </summary>
    [Fact]
    public void AnUncoveredTriple_ResolvesNoMapper_AndThrowsOnUse()
    {
        using var provider = Host();
        using var scope = provider.CreateScope();

        var repo = new ShiftRepository<OrderingDbContext, OrderEntity, UnregisteredOrderDTO, UnregisteredOrderDTO>(
            scope.ServiceProvider.GetRequiredService<OrderingDbContext>());

        Assert.Null(repo.ShiftRepositoryOptions.Mapper);

        var ex = Assert.Throws<InvalidOperationException>(() => repo.MapToView(new OrderEntity()));
        Assert.Contains("No mapper configured", ex.Message);
    }

    /// <summary>
    /// Generated mappers carry per-instance builder state that <c>AddConfiguration</c> mutates, so the
    /// activator is cached and the instance never is. A shared singleton would leak one repository's
    /// customization into every other consumer of the triple — a cross-request bug that would present as
    /// intermittent mis-mapping.
    /// </summary>
    [Fact]
    public void EachRepository_GetsItsOwnMapperInstance()
    {
        RegisterGeneratedMapper();

        using var provider = Host();
        using var scope = provider.CreateScope();

        var first = Repo(scope).ShiftRepositoryOptions.Mapper;
        var second = Repo(scope).ShiftRepositoryOptions.Mapper;

        Assert.NotNull(first);
        Assert.NotSame(first, second);
    }

    /// <summary>An explicitly configured mapper still wins — the registry only supplies the DEFAULT.</summary>
    [Fact]
    public void AnExplicitMapper_StillBeatsTheRegistry()
    {
        RegisterGeneratedMapper();

        using var provider = Host();
        using var scope = provider.CreateScope();

        var explicitMapper = new RegistryOrderMapper();
        var repo = new ShiftRepository<OrderingDbContext, OrderEntity, OrderListDTO, OrderListDTO>(
            scope.ServiceProvider.GetRequiredService<OrderingDbContext>(),
            o => o.UseMapper(explicitMapper));

        Assert.Same(explicitMapper, repo.ShiftRepositoryOptions.Mapper);
    }
}
