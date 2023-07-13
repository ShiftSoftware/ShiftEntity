namespace ShiftSoftware.ShiftEntity.Model.HashId;

internal class RegionHashIdConverter : JsonHashIdConverterAttribute<RegionHashIdConverter>
{
    public RegionHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
