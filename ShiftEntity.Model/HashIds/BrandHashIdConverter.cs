namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class BrandHashIdConverter : JsonHashIdConverterAttribute<BrandHashIdConverter>
{
    // Identity hasher: salt/minLength/alphabet resolved from HashIdOptions at hasher-build time
    // by HashIdService.GetHasherFor (detects isIdentityHasher == true). No static reads.
    public BrandHashIdConverter() : base(isIdentityHasher: true)
    {
    }
}
