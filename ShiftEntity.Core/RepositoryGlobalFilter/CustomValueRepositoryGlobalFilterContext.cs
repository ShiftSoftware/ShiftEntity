namespace ShiftSoftware.ShiftEntity.Core.RepositoryGlobalFilter;

public class CustomValueRepositoryGlobalFilterContext<TEntity, TValue> where TEntity : ShiftEntity<TEntity>
{
    public TEntity Entity { get; } = default!;
    public TValue CustomValue { get; } = default!;
}