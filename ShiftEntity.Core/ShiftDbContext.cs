using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

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
        optionsBuilder.AddDelegateDecompiler();

        base.OnConfiguring(optionsBuilder);
    }
}
