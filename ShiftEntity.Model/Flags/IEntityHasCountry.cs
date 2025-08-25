namespace ShiftSoftware.ShiftEntity.Model.Flags;

public interface IEntityHasCountry<Entity>
{
    long? CountryID { get; set; }
}
