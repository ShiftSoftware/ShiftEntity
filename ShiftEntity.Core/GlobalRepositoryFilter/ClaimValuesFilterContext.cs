using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core.GlobalRepositoryFilter;

public class ClaimValuesFilterContext<TEntity> where TEntity : ShiftEntity<TEntity>
{
    public TEntity Entity { get; } = default!;
    public List<string>? ClaimValues { get; set; }
}