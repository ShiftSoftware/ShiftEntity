using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore.Entities;

namespace ShiftSoftware.ShiftEntity.EFCore;

public abstract class ShiftDbContext : DbContext
{
    internal ShiftDbContextExtensionOptions? ShiftDbContextOptions { get; set; }

    public DbSet<DeletedRowLog> DeletedRowLogs { get; set; }

    public ShiftDbContext() : base()
    {
    }

    public ShiftDbContext(DbContextOptions options) : base(options)
    {
        ShiftDbContextOptions = options.Extensions
            .OfType<ShiftDbContextExtensionOptions>()
            .FirstOrDefault();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ConfigureShiftEntity(ShiftDbContextOptions?.UseTemporal ?? false);

        // Invoke all registered IModelBuildingContributor instances
        var contributors = this.GetService<IEnumerable<IModelBuildingContributor>>();
        if (contributors is not null)
        {
            foreach (var contributor in contributors)
            {
                contributor.Configure(modelBuilder);
            }
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
    }
}
