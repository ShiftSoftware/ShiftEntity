namespace ShiftSoftware.ShiftEntity.Model.Flags;

/// <summary>Non-generic seam carrying <c>CityID</c> so the repository can stamp it on any entity, regardless of its closed generic type.</summary>
public interface IEntityHasCity
{
    long? CityID { get; set; }
}

public interface IEntityHasCity<Entity> : IEntityHasCity
{
}
