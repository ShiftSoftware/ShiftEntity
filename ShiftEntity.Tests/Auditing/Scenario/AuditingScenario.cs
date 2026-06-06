using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;

namespace ShiftSoftware.ShiftEntity.Tests.Auditing.Scenario;

// ── Entities ───────────────────────────────────────────────────────────────

/// <summary>
/// Aggregate root for the audit tests. Owns a collection of <see cref="OrderLineEntity"/> children of a
/// <em>different</em> <c>ShiftEntity&lt;T&gt;</c> type — the shape that exercises whether
/// <c>SaveChangesAsync</c> on the parent repository stamps audit fields on cascaded children.
/// </summary>
public class OrderEntity : ShiftEntity<OrderEntity>
{
    public string Number { get; set; } = "";
    public List<OrderLineEntity> Lines { get; set; } = new();
}

/// <summary>A child row of a <em>different</em> entity type than its parent <see cref="OrderEntity"/>.</summary>
public class OrderLineEntity : ShiftEntity<OrderLineEntity>
{
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public long OrderID { get; set; }
}

/// <summary>
/// Self-referencing entity: a category's children are the <em>same</em> <c>ShiftEntity&lt;T&gt;</c> type as the
/// parent. This is the contrasting case to <see cref="OrderEntity"/> — here a cascaded child <em>is</em> a
/// <c>ShiftEntity&lt;CategoryEntity&gt;</c>, so the repository's type filter accepts it.
/// </summary>
public class CategoryEntity : ShiftEntity<CategoryEntity>
{
    public string Name { get; set; } = "";
    public long? ParentID { get; set; }
    public CategoryEntity? Parent { get; set; }
    public List<CategoryEntity> Children { get; set; } = new();
}

// ── DbContext ───────────────────────────────────────────────────────────────

/// <summary>EF context for the auditing tests — a parent/child aggregate and a self-referencing tree.</summary>
public class OrderingDbContext : ShiftDbContext
{
    public DbSet<OrderEntity> Orders { get; set; } = default!;
    public DbSet<OrderLineEntity> OrderLines { get; set; } = default!;
    public DbSet<CategoryEntity> Categories { get; set; } = default!;

    public OrderingDbContext(DbContextOptions<OrderingDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // ShiftDbContext.OnModelCreating runs ConfigureShiftEntity

        // Pin OrderID / ParentID as the real foreign keys (otherwise EF convention, which keys off the principal
        // *type* name "OrderEntity"/"CategoryEntity", would create shadow FKs and leave our properties at 0).
        modelBuilder.Entity<OrderEntity>()
            .HasMany(o => o.Lines)
            .WithOne()
            .HasForeignKey(l => l.OrderID);

        modelBuilder.Entity<CategoryEntity>()
            .HasMany(c => c.Children)
            .WithOne(c => c.Parent!)
            .HasForeignKey(c => c.ParentID);
    }
}

// ── DTOs ────────────────────────────────────────────────────────────────────

/// <summary>Minimal DTO — the repository's type parameters demand one. The audit tests never map.</summary>
public class OrderListDTO : ShiftEntityDTOBase
{
    public override string? ID { get; set; }
    public string Number { get; set; } = "";
}

public class OrderLineListDTO : ShiftEntityDTOBase
{
    public override string? ID { get; set; }
    public string Sku { get; set; } = "";
}

public class CategoryListDTO : ShiftEntityDTOBase
{
    public override string? ID { get; set; }
    public string Name { get; set; } = "";
}

// ── Test doubles ──────────────────────────────────────────────────────────────

/// <summary>
/// <see cref="IShiftEntityMapper{TEntity,TListDTO,TViewDTO}"/> double whose every member throws — the audit/save
/// path never maps, so any call is a wiring mistake we want to fail loud on.
/// </summary>
public sealed class ThrowingMapper<TEntity, TDto> : IShiftEntityMapper<TEntity, TDto, TDto>
    where TEntity : ShiftEntity<TEntity>, new()
    where TDto : ShiftEntityDTOBase
{
    public TDto MapToView(TEntity entity) => throw new NotSupportedException();
    public TEntity MapToEntity(TDto dto, TEntity existing) => throw new NotSupportedException();
    public IQueryable<TDto> MapToList(IQueryable<TEntity> query) => throw new NotSupportedException();
    public void CopyEntity(TEntity source, TEntity target) => throw new NotSupportedException();
}

/// <summary>
/// <see cref="IHashIdService"/> double that treats hashids as the raw decimal string (identity), including the
/// <c>Decode(string, JsonHashIdConverterAttribute)</c> overload the user-id claim path uses — so a test can supply
/// a known user id through a <see cref="ClaimTypes.NameIdentifier"/> claim. Members the save path never touches throw.
/// </summary>
public sealed class IdentityHashIdService : IHashIdService
{
    public long Decode(string key, Type dtoType) => long.Parse(key);
    public string Encode(long id, Type dtoType) => id.ToString();
    public long Decode(string key, JsonHashIdConverterAttribute attr) => long.Parse(key);

    public bool IsConfigurationRegistered(string configurationName) => throw new NotImplementedException();
    public bool IsAcceptUnencodedIds(string? configurationName) => throw new NotImplementedException();
    public long Decode<TDTO>(string key) => throw new NotImplementedException();
    public string Encode<TDTO>(long id) => throw new NotImplementedException();
    public string Encode(long id, JsonHashIdConverterAttribute attr) => throw new NotImplementedException();
    public ShiftEntityHashId? GetHasherFor(JsonHashIdConverterAttribute attr) => throw new NotImplementedException();
}

/// <summary>
/// <see cref="ICurrentUserProvider"/> double — either anonymous (no signed-in user ⇒ null user id) or a caller
/// whose principal carries a <see cref="ClaimTypes.NameIdentifier"/> claim that
/// <see cref="IdentityHashIdService"/> decodes back to <paramref name="userId"/>.
/// </summary>
public sealed class FakeUserProvider : ICurrentUserProvider
{
    private readonly ClaimsPrincipal? user;
    private FakeUserProvider(ClaimsPrincipal? user) => this.user = user;
    public ClaimsPrincipal? GetUser() => user;

    public static FakeUserProvider Anonymous() => new(null);

    public static FakeUserProvider WithUserId(long userId)
    {
        var identity = new ClaimsIdentity(authenticationType: "Test");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        return new FakeUserProvider(new ClaimsPrincipal(identity));
    }
}

// ── DI host ───────────────────────────────────────────────────────────────────

/// <summary>
/// DI host for the audit tests: EF InMemory <see cref="OrderingDbContext"/> (fresh database per host), the identity
/// <see cref="IHashIdService"/>, a configurable <see cref="ICurrentUserProvider"/>, and an
/// <see cref="IdentityClaimProvider"/>. No data-level access is wired — the save/audit path does not consult it.
/// </summary>
public static class AuditingHost
{
    public static ServiceProvider Build(Func<ICurrentUserProvider>? currentUser = null)
    {
        var services = new ServiceCollection();

        services.AddDbContext<OrderingDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        services.AddScoped<ICurrentUserProvider>(_ => (currentUser ?? FakeUserProvider.Anonymous)());
        services.AddScoped<IdentityClaimProvider>();
        services.AddSingleton<IHashIdService>(new IdentityHashIdService());
        // The repository constructor resolves IDefaultDataLevelAccess from the context's service provider; the
        // save/audit path never calls it, so the recording double (permissive) just satisfies the dependency.
        services.AddSingleton<IDefaultDataLevelAccess>(new RecordingDefaultDataLevelAccess());

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
