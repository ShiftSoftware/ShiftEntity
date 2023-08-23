using Microsoft.EntityFrameworkCore;
using ShiftSoftware.EFCore.SqlServer.Extensions;
using ShiftSoftware.ShiftEntity.EFCore.SqlServer;
using ShiftSoftware.ShiftEntity.EFCore.SqlServer.Entities;

namespace ShiftSoftware.EFCore.SqlServer;

public abstract class ShiftDbContext : DbContext
{
    internal ShiftDbContextExtensionOptions? ShiftDbContextOptions { get; set; }

    public DbSet<DeletedRowLog> DeletedRowLogs { get; set; }

    public ShiftDbContext() : base()
    {
    }

    public ShiftDbContext(DbContextOptions options) : base(options)
    {
        this.ShiftDbContextOptions = options.Extensions
            .OfType<ShiftDbContextExtensionOptions>()
            .FirstOrDefault();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ConfigureShiftEntity(ShiftDbContextOptions?.UseTemporal ?? false);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseTriggers();

        base.OnConfiguring(optionsBuilder);
    }
}
