namespace ShiftSoftware.ShiftEntity.Model.Flags;

/// <summary>Non-generic seam carrying <c>CountryID</c> so the repository can stamp it on any entity, regardless of its closed generic type.</summary>
public interface IEntityHasCountry
{
    long? CountryID { get; set; }
}

public interface IEntityHasCountry<Entity> : IEntityHasCountry
{
}
