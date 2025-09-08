using System;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.Core.RepositoryGlobalFilter;

public interface IRepositoryGlobalFilter
{
    public Guid ID { get; set; }
    public bool Disabled { get; set; }
    Expression<Func<T, bool>>? GetFilterExpression<T>() where T : ShiftEntity<T>;
}