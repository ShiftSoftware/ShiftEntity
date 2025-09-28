namespace ShiftSoftware.ShiftEntity.Core.GlobalRepositoryFilter;

public class CustomValueFilterContext<TEntity, TValue> where TEntity : ShiftEntity<TEntity>
{
    public TEntity Entity { get; } = default!;
    public TValue CustomValue { get; } = default!;
}