namespace ShiftSoftware.ShiftEntity.Core.Flags;

public interface IEntityHasUniqueHash<Entity> : IEntityHasUniqueHash
    where Entity : ShiftEntityBase, new()
{

}

public interface IEntityHasUniqueHash
{
    public string? CalculateUniqueHash();
}