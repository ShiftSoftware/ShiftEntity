namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class CityHashIdConverter : JsonHashIdConverterAttribute<CityHashIdConverter>
{
    public CityHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}