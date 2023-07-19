using Microsoft.EntityFrameworkCore;
using ShiftSoftware.EFCore.SqlServer.Extensions;
using ShiftSoftware.ShiftEntity.EFCore.SqlServer.Triggers;

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
        optionsBuilder.UseTriggers(x=> x.AddTrigger<GeneralTrigger>());

        base.OnConfiguring(optionsBuilder);
    }
}
