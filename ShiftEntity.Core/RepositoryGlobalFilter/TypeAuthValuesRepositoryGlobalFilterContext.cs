using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core.RepositoryGlobalFilter;

public class TypeAuthValuesRepositoryGlobalFilterContext<TEntity> where TEntity : ShiftEntity<TEntity>
{
    public TEntity Entity { get; } = default!;
    public bool WildCardRead { get; set; }
    public bool WildCardWrite { get; set; }
    public bool WildCardDelete { get; set; }
    public bool WildCardMaxAccess { get; set; }
    public List<string>? ReadableTypeAuthValues { get; set; }
    public List<string>? WritableTypeAuthValues { get; set; }
    public List<string>? DeletableTypeAuthValues { get; set; }
    public List<string>? MaxAccessTypeAuthValues { get; set; }
}