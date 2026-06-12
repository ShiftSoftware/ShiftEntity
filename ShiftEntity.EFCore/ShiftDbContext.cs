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

    /// <summary>
    /// Backing state for <see cref="SuppressAuditStamping"/>: <see langword="true"/> while at least one suppression
    /// scope is alive, making the SaveChanges override skip its insert backfill. Only the scope touches this — every
    /// caller, the repository included, goes through <see cref="SuppressAuditStamping"/>.
    /// </summary>
    private bool AuditStampingSuppressed { get; set; }

    /// <summary>
    /// Suppresses every audit backfill this context's SaveChanges override would perform until the returned scope is
    /// disposed. Two kinds of callers use it: <see cref="ShiftRepository{DB,EntityType,ListDTO,ViewAndUpsertDTO}"/>
    /// wraps its own save in it because its sweep has already stamped the tracked entries, and infrastructure saves
    /// (replication bookkeeping, sync/import jobs, maintenance scripts) use it to write exactly the columns they
    /// touched and nothing else. Composes with any save method, including <c>SaveChangesWithoutTriggersAsync</c>:
    /// <code>
    /// using (db.SuppressAuditStamping())
    ///     await db.SaveChangesWithoutTriggersAsync();
    /// </code>
    /// Disposal restores the previous suppression state, so scopes nest safely.
    /// </summary>
    public IDisposable SuppressAuditStamping() => new AuditStampingSuppressionScope(this);

    private sealed class AuditStampingSuppressionScope : IDisposable
    {
        private readonly ShiftDbContext context;
        private readonly bool previous;

        public AuditStampingSuppressionScope(ShiftDbContext context)
        {
            this.context = context;
            previous = context.AuditStampingSuppressed;
            context.AuditStampingSuppressed = true;
        }

        public void Dispose() => context.AuditStampingSuppressed = previous;
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
    /// Backfills the audit columns and creation claims on every <b>inserted</b> auditable row that has not already
    /// been stamped — the fallback for entities added directly through the context rather than through a repository.
    /// Each value fills only where the caller left it unset, so a manually-assigned value always wins. (A repository
    /// both stamps the rows and holds a <see cref="SuppressAuditStamping"/> scope around its save, so
    /// repository-routed saves skip this entirely, and the <c>AuditFieldsAreSet</c> guard skips any individually
    /// pre-stamped row.)
    ///
    /// <para>Deliberately insert-only. Rows that are merely <b>modified</b> through a bare context are left
    /// untouched: in practice those saves are system writes — sync jobs, enrichment, replication bookkeeping —
    /// where silently advancing <c>LastSaveDate</c> and overwriting <c>LastSavedByUserID</c> (with null, in a
    /// background scope) would destroy real audit information. User-facing updates flow through
    /// <see cref="ShiftRepository{DB,EntityType,ListDTO,ViewAndUpsertDTO}"/>, whose sweep stamps them; a
    /// non-repository update path that genuinely wants stamping opts in explicitly via
    /// <see cref="AuditStamper.StampAuditFields"/> (see <c>AttentionPipeline.ClearSignals</c> for the pattern).</para>
    ///
    /// <para>Every value is resolved <b>defensively</b>: if the claim service isn't registered, or a value is absent
    /// or can't be read, it is treated as null/default rather than throwing — a bare context (migrations, a unit test
    /// without DI) still saves, just without a user/org stamp.</para>
    /// </summary>
    private void StampAuditColumns()
    {
        if (AuditStampingSuppressed)
            return; // a repository already stamped these rows, or an infrastructure save opted out

        var now = DateTimeOffset.UtcNow;

        var resolved = false;
        long? userId = null, countryId = null, regionId = null, cityId = null, companyId = null, branchId = null;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added)
                continue; // insert-only by design — see the remarks above

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

            AuditStamper.StampAuditFields(auditable, isAdded: true, userId, now);
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
