using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityHasAfterSaveHook<T>
{
    ValueTask AfterSaveAsync(T entity, ActionTypes action);
}