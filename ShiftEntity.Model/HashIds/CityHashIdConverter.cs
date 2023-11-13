namespace ShiftSoftware.ShiftEntity.Model.HashIds;

internal class CityHashIdConverter : JsonHashIdConverterAttribute<CityHashIdConverter>
{
    public CityHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}