using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.CosmosDbSync;

public class DbContextOptionsProvider
{
    public DbContextOptions DbContextOptions { get;internal set; }

    public DbContextOptionsProvider(DbContextOptions dbContextOptions)
    {
        this.DbContextOptions = dbContextOptions;
    }
}
