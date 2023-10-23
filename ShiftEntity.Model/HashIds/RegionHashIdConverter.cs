namespace ShiftSoftware.ShiftEntity.Model.HashIds;

internal class RegionHashIdConverter : JsonHashIdConverterAttribute<RegionHashIdConverter>
{
    public RegionHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
