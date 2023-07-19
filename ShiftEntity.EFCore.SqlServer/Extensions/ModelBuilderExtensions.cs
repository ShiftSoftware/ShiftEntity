using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using System.Linq.Expressions;
using static Microsoft.EntityFrameworkCore.Query.ReplacingExpressionVisitor;

namespace ShiftSoftware.EFCore.SqlServer.Extensions;

public static class ModelBuilderExtensions
{
    public static ModelBuilder ConfigureShiftEntity(this ModelBuilder modelBuilder)
    {
        Expression<Func<ShiftEntityBase, bool>> filterExpr = bm => !bm.IsDeleted;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            var isTemporal = clrType.GetCustomAttributes(true).LastOrDefault(x => x as TemporalShiftEntity != null);

            if (isTemporal != null)
            {
                //Make the tables temporal that has TemporalShiftEntyty attribute 
                modelBuilder.Entity(entityType.ClrType).ToTable(b => b.IsTemporal());
            }

            if (clrType.IsAssignableTo(typeof(ShiftEntityBase)))
            {
                //Golobaly filter soft deleted rows
                var parameter = Expression.Parameter(clrType);
                var body = Replace(filterExpr.Parameters.First(), parameter, filterExpr.Body);
                var lambdaExpression = Expression.Lambda(body, parameter);
                entityType.SetQueryFilter(lambdaExpression);
            }
        }

        ///// Disable Cascade Delete
        var cascadeFKs = modelBuilder.Model.GetEntityTypes()
                .SelectMany(t => t.GetForeignKeys())
                .Where(fk => !fk.IsOwnership && fk.DeleteBehavior == DeleteBehavior.Cascade);

        foreach (var fk in cascadeFKs)
            fk.DeleteBehavior = DeleteBehavior.Restrict;

        return modelBuilder;
    }

}
