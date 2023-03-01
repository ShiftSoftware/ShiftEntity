using DelegateDecompiler;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using System;
using System.Linq;
using System.Linq.Expressions;
using static Microsoft.EntityFrameworkCore.Query.ReplacingExpressionVisitor;

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

        ///// Disable Cascade Delete
        var cascadeFKs = modelBuilder.Model.GetEntityTypes()
                .SelectMany(t => t.GetForeignKeys())
                .Where(fk => !fk.IsOwnership && fk.DeleteBehavior == DeleteBehavior.Cascade);

        foreach (var fk in cascadeFKs)
            fk.DeleteBehavior = DeleteBehavior.Restrict;

        return modelBuilder;
    }

    public static DbContextOptionsBuilder AddDelegateDecompiler(this DbContextOptionsBuilder optionsBuilder)
    { 
        return optionsBuilder.AddInterceptors(new DelegateDecompilerQueryPreprocessor());
    }

    public class DelegateDecompilerQueryPreprocessor : DecompileExpressionVisitor, IQueryExpressionInterceptor
    {
        Expression IQueryExpressionInterceptor.QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData) => Visit(queryExpression);

        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (node.NodeType == ExpressionType.Convert && node.Method != null)
            {
                var decompiled = node.Method.Decompile();
                return Replace(decompiled.Parameters[0], Visit(node.Operand), decompiled.Body);
            }

            return base.VisitUnary(node);
        }
    }
}
