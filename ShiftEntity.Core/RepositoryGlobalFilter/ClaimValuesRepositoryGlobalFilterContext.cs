using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core.RepositoryGlobalFilter;

public class ClaimValuesRepositoryGlobalFilterContext<TEntity> where TEntity : ShiftEntity<TEntity>
{
    public TEntity Entity { get; } = default!;
    public List<string>? ClaimValues { get; set; }
}