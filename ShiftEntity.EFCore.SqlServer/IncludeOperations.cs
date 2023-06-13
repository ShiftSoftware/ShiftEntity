using Microsoft.EntityFrameworkCore.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.EFCore.SqlServer;


public class IncludeOperations<TEntity>
    where TEntity : class
{
    internal string Includes { get; set; }

    public IncludeOperations<TEntity, TProperty> Include<TProperty>(Expression<Func<TEntity, TProperty>> navigartionProperyPath)
    {
        Includes = navigartionProperyPath.GetMemberAccess().Name;
        return new IncludeOperations<TEntity, TProperty>(this);
    }
}

public class IncludeOperations<TEntity, TPreviousProperty>
    where TEntity : class
{
    internal IncludeOperations<TEntity> Parent { get; }

    public IncludeOperations(IncludeOperations<TEntity> parent)
    {
        Parent = parent;
    }
}

public static class IncludeOperationsExtensions
{
    public static IncludeOperations<TEntity, TProperty> ThenInclude<TEntity, TPreviousProperty, TProperty>
        (this IncludeOperations<TEntity, List<TPreviousProperty>> source,
        Expression<Func<TPreviousProperty, TProperty>> navigartionProperyPath)
        where TEntity : class
    {
        source.Parent.Includes += "." + navigartionProperyPath.GetMemberAccess().Name;
        return new IncludeOperations<TEntity, TProperty>(source.Parent);
    }

    public static IncludeOperations<TEntity, TProperty> ThenInclude<TEntity, TPreviousProperty, TProperty>
        (this IncludeOperations<TEntity, IEnumerable<TPreviousProperty>> source,
        Expression<Func<TPreviousProperty, TProperty>> navigartionProperyPath)
        where TEntity : class
    {
        source.Parent.Includes += "." + navigartionProperyPath.GetMemberAccess().Name;
        return new IncludeOperations<TEntity, TProperty>(source.Parent);
    }

    public static IncludeOperations<TEntity, TProperty> ThenInclude<TEntity, TPreviousProperty, TProperty>
        (this IncludeOperations<TEntity, ICollection<TPreviousProperty>> source,
        Expression<Func<TPreviousProperty, TProperty>> navigartionProperyPath)
        where TEntity : class
    {
        source.Parent.Includes += "." + navigartionProperyPath.GetMemberAccess().Name;
        return new IncludeOperations<TEntity, TProperty>(source.Parent);
    }

    public static IncludeOperations<TEntity, TProperty> ThenInclude<TEntity, TPreviousProperty, TProperty>
        (this IncludeOperations<TEntity, TPreviousProperty> source,
        Expression<Func<TPreviousProperty, TProperty>> navigartionProperyPath)
        where TEntity : class
    {
        source.Parent.Includes += "." + navigartionProperyPath.GetMemberAccess().Name;
        return new IncludeOperations<TEntity, TProperty>(source.Parent);
    }
}
