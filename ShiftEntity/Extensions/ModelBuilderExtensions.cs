using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection.Emit;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Extensions;

public static class ModelBuilderExtensions
{
    public static ModelBuilder ConfigureShiftEntity(this ModelBuilder modelBuilder)
    {
        Expression<Func<IShiftEntity, bool>> filterExpr = bm => !bm.IsDeleted;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (clrType.IsAssignableTo(typeof(IShiftEntity)))
            {
                var isTemporal = clrType.GetCustomAttributes(true).LastOrDefault(x => (x as TemporalShiftEntity) != null);

                if (isTemporal != null)
                {
                    //Make the tables temporal that has TemporalShiftEntyty attribute 
                    modelBuilder.Entity(entityType.ClrType).ToTable(b => b.IsTemporal());
                }

                //Golobaly filter soft deleted rows
                var parameter = Expression.Parameter(clrType);
                var body = ReplacingExpressionVisitor.Replace(filterExpr.Parameters.First(), parameter, filterExpr.Body);
                var lambdaExpression = Expression.Lambda(body, parameter);
                entityType.SetQueryFilter(lambdaExpression);
            }
        }
        
        return modelBuilder;
    }
}
