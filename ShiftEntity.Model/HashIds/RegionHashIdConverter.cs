namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class RegionHashIdConverter : JsonHashIdConverterAttribute<RegionHashIdConverter>
{
    // Identity hasher: salt/minLength/alphabet resolved from HashIdOptions at hasher-build time
    // by HashIdService.GetHasherFor (detects isIdentityHasher == true). No static reads.
    public RegionHashIdConverter() : base(isIdentityHasher: true)
    {
    }
}
