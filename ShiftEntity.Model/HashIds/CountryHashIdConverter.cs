namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class CountryHashIdConverter : JsonHashIdConverterAttribute<CountryHashIdConverter>
{
    public CountryHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
