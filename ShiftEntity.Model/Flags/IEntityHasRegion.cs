namespace ShiftSoftware.ShiftEntity.Model.Flags;

/// <summary>Non-generic seam carrying <c>RegionID</c> so the repository can stamp it on any entity, regardless of its closed generic type.</summary>
public interface IEntityHasRegion
{
    long? RegionID { get; set; }
}

public interface IEntityHasRegion<Entity> : IEntityHasRegion
{
}
