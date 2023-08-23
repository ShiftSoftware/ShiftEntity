using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.EFCore;


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
