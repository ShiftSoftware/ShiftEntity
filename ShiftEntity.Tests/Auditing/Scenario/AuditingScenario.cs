using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.Flags;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;

namespace ShiftSoftware.ShiftEntity.Tests.Auditing.Scenario;

// ── Entities ───────────────────────────────────────────────────────────────

/// <summary>
/// Test-only non-generic accessor for the org/location claim columns, so a single assertion helper can read them
/// across every (differently-typed) entity. The framework's <c>IEntityHas*</c> flags are generic, so they can't
/// serve that role directly.
/// </summary>
public interface IHasOrgClaims
{
    long? CompanyID { get; set; }
    long? CompanyBranchID { get; set; }
    long? CountryID { get; set; }
    long? RegionID { get; set; }
    long? CityID { get; set; }
}

/// <summary>
/// Marker bundling the framework org/location flag interfaces (what a real entity implements) with the test-only
/// non-generic <see cref="IHasOrgClaims"/> accessor, so the test entities carry both.
/// </summary>
public interface IHasOrgClaims<T> : IHasOrgClaims,
    IEntityHasCompany<T>, IEntityHasCompanyBranch<T>, IEntityHasCountry<T>, IEntityHasRegion<T>, IEntityHasCity<T>
{ }

/// <summary>
/// Aggregate root for the audit tests. Owns a collection of <see cref="OrderLineEntity"/> children of a
/// <em>different</em> <c>ShiftEntity&lt;T&gt;</c> type — the shape that exercises whether
/// <c>SaveChangesAsync</c> on the parent repository stamps audit fields on cascaded children. Carries the org/location
/// claim columns so the tests can assert the SaveChanges sweep backfills those too.
/// </summary>
public class OrderEntity : ShiftEntity<OrderEntity>, IHasOrgClaims<OrderEntity>
{
    public string Number { get; set; } = "";
    public List<OrderLineEntity> Lines { get; set; } = new();

    public long? CompanyID { get; set; }
    public long? CompanyBranchID { get; set; }
    public long? CountryID { get; set; }
    public long? RegionID { get; set; }
    public long? CityID { get; set; }
}

/// <summary>A child row of a <em>different</em> entity type than its parent <see cref="OrderEntity"/>.</summary>
public class OrderLineEntity : ShiftEntity<OrderLineEntity>, IHasOrgClaims<OrderLineEntity>
{
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public long OrderID { get; set; }

    public long? CompanyID { get; set; }
    public long? CompanyBranchID { get; set; }
    public long? CountryID { get; set; }
    public long? RegionID { get; set; }
    public long? CityID { get; set; }
}

/// <summary>
/// Self-referencing entity: a category's children are the <em>same</em> <c>ShiftEntity&lt;T&gt;</c> type as the
/// parent. This is the contrasting case to <see cref="OrderEntity"/> — here a cascaded child <em>is</em> a
/// <c>ShiftEntity&lt;CategoryEntity&gt;</c>, so the repository's type filter accepts it.
/// </summary>
public class CategoryEntity : ShiftEntity<CategoryEntity>, IHasOrgClaims<CategoryEntity>
{
    public string Name { get; set; } = "";
    public long? ParentID { get; set; }
    public CategoryEntity? Parent { get; set; }
    public List<CategoryEntity> Children { get; set; } = new();

    public long? CompanyID { get; set; }
    public long? CompanyBranchID { get; set; }
    public long? CountryID { get; set; }
    public long? RegionID { get; set; }
    public long? CityID { get; set; }
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

    public static FakeUserProvider WithUserId(long userId) => WithClaims(userId: userId);

    /// <summary>
    /// A signed-in caller carrying any subset of the audit claims (user id plus the org/location claims the audit
    /// sweep backfills). Each non-null value becomes a claim that <see cref="IdentityHashIdService"/> decodes back to
    /// the same number.
    /// </summary>
    public static FakeUserProvider WithClaims(
        long? userId = null, long? companyId = null, long? branchId = null,
        long? countryId = null, long? regionId = null, long? cityId = null)
    {
        var identity = new ClaimsIdentity(authenticationType: "Test");

        void Add(string type, long? value)
        {
            if (value is not null)
                identity.AddClaim(new Claim(type, value.Value.ToString()));
        }

        Add(ClaimTypes.NameIdentifier, userId);
        Add(Constants.CompanyIdClaim, companyId);
        Add(Constants.CompanyBranchIdClaim, branchId);
        Add(Constants.CountryIdClaim, countryId);
        Add(Constants.RegionIdClaim, regionId);
        Add(Constants.CityIdClaim, cityId);

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
