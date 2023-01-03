using DelegateDecompiler;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
