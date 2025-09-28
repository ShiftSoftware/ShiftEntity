using System;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.GlobalRepositoryFilter;

public interface IGlobalRepositoryFilter
{
    public Guid ID { get; }
    public bool Disabled { get; set; }
    public ValueTask<Expression<Func<T, bool>>?> GetFilterExpression<T>() where T : ShiftEntity<T>;
}