using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore.Entities;

namespace ShiftSoftware.ShiftEntity.EFCore;

public abstract class ShiftDbContext : DbContext
{
    internal ShiftDbContextExtensionOptions? ShiftDbContextOptions { get; set; }

    public DbSet<DeletedRowLog> DeletedRowLogs { get; set; }

    public ShiftDbContext() : base()
    {
    }

    private readonly IServiceProvider? _applicationServiceProvider;

    internal IServiceProvider? ApplicationServiceProvider => _applicationServiceProvider;

    public ShiftDbContext(DbContextOptions options) : base(options)
    {
        ShiftDbContextOptions = options.Extensions
            .OfType<ShiftDbContextExtensionOptions>()
            .FirstOrDefault();

        _applicationServiceProvider = options.Extensions
            .OfType<CoreOptionsExtension>()
            .FirstOrDefault()
            ?.ApplicationServiceProvider;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Invoke contributors before ConfigureShiftEntity so that ConfigureShiftEntity
        // can apply conventions (e.g. DeleteBehavior.Restrict) to all relationships,
        // including those added by contributors.
        var contributors = _applicationServiceProvider?.GetService<IEnumerable<IModelBuildingContributor>>();
        if (contributors is not null)
        {
            foreach (var contributor in contributors)
            {
                contributor.Configure(modelBuilder);
            }
        }

        modelBuilder.ConfigureShiftEntity(ShiftDbContextOptions?.UseTemporal ?? false);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampAuditColumns();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampAuditColumns();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Backfills the audit columns on every changed auditable row that has not already been stamped — the fallback
    /// for entities saved directly through the context rather than through a repository. A repository sets the
    /// <c>AuditFieldsAreSet</c> guard, so repository-routed saves pass through here untouched (no double-stamping).
    ///
    /// <para>Every value is resolved <b>defensively</b>: if the claim service isn't registered, or a value is absent
    /// or can't be read, it is treated as null/default rather than throwing — a bare context (migrations, a unit test
    /// without DI) still saves, just without a user/org stamp.</para>
    /// </summary>
    private void StampAuditColumns()
    {
        var now = DateTimeOffset.UtcNow;

        var resolved = false;
        long? userId = null, countryId = null, regionId = null, cityId = null, companyId = null, branchId = null;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added && entry.State != EntityState.Modified)
                continue;

            if (entry.Entity is not IShiftEntityAudit auditable || auditable.AuditFieldsAreSet)
                continue; // not auditable, or already stamped upstream (e.g. by the repository)

            // Resolve the acting user's claims once, only when there is actually a row to stamp.
            if (!resolved)
            {
                var claims = TryResolveClaims();
                userId = SafeClaim(claims, c => c.GetUserID());
                countryId = SafeClaim(claims, c => c.GetCountryID());
                regionId = SafeClaim(claims, c => c.GetRegionID());
                cityId = SafeClaim(claims, c => c.GetCityID());
                companyId = SafeClaim(claims, c => c.GetCompanyID());
                branchId = SafeClaim(claims, c => c.GetCompanyBranchID());
                resolved = true;
            }

            var added = entry.State == EntityState.Added;

            AuditStamper.StampAuditFields(auditable, added, userId, now);

            if (added)
                AuditStamper.StampCreationClaims(entry.Entity, countryId, regionId, cityId, companyId, branchId);
        }
    }

    /// <summary>Resolves <see cref="IdentityClaimProvider"/> if available, returning null instead of throwing.</summary>
    private IdentityClaimProvider? TryResolveClaims()
    {
        try { return this.GetService<IdentityClaimProvider>(); }
        catch { return null; }
    }

    private static long? SafeClaim(IdentityClaimProvider? claims, Func<IdentityClaimProvider, long?> get)
    {
        if (claims is null)
            return null;

        try { return get(claims); }
        catch { return null; }
    }
}
