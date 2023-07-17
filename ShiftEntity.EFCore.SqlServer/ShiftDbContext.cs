using Microsoft.EntityFrameworkCore;
using ShiftSoftware.EFCore.SqlServer.Extensions;


namespace ShiftSoftware.EFCore.SqlServer;

public abstract class ShiftDbContext : DbContext
{
    public ShiftDbContext() : base()
    {
    }

    public ShiftDbContext(DbContextOptions options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ConfigureShiftEntity();
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
    }
}
