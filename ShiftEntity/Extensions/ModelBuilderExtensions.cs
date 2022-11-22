using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Extensions;

public static class ModelBuilderExtensions
{
    public static ModelBuilder ConfigureShiftEntity(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (clrType.IsAssignableTo(typeof(IShiftEntity)))
            {
                var isTemporal = clrType.GetCustomAttributes(true).LastOrDefault(x => (x as TemporalShiftEntity) != null);

                if (isTemporal != null)
                {
                    modelBuilder.Entity(entityType.ClrType).ToTable(b => b.IsTemporal());
                }
            }
        }

        return modelBuilder;
    }
}
