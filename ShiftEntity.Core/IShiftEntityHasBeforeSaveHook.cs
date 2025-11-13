using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityHasBeforeSaveHook<T>
{
    ValueTask BeforeSaveAsync(T entity, ActionTypes action);
}