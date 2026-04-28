namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class BrandHashIdConverter : JsonHashIdConverterAttribute<BrandHashIdConverter>
{
    public BrandHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
