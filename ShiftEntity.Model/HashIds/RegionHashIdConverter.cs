namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class RegionHashIdConverter : JsonHashIdConverterAttribute<RegionHashIdConverter>
{
    public RegionHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
