using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using ShiftSoftware.ShiftEntity.EFCore.Tagging;

namespace ShiftSoftware.ShiftEntity.EFCore;

public abstract class ShiftDbContext : DbContext
{
    internal ShiftDbContextExtensionOptions? ShiftDbContextOptions { get; set; }

    public DbSet<DeletedRowLog> DeletedRowLogs { get; set; }

    /// <summary>
    /// Tag vocabulary for this DbContext. Ignored from the EF model — and therefore
    /// from migrations — unless the application calls <c>services.AddShiftTagging&lt;TDbContext&gt;()</c>.
    /// Access this set without registering tagging will throw at runtime.
    /// </summary>
    public DbSet<Tag> Tags { get; set; } = default!;

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

        var taggingOptions = _applicationServiceProvider?.GetService<IOptions<ShiftTaggingOptions>>()?.Value;
        var effectiveTaggingOptions = taggingOptions?.Enabled == true ? taggingOptions : null;

        modelBuilder.ConfigureShiftEntity(ShiftDbContextOptions?.UseTemporal ?? false, effectiveTaggingOptions);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
    }
}
