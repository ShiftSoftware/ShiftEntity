namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class AccessTreeHashIdConverter : JsonHashIdConverterAttribute<AccessTreeHashIdConverter>
{
    // Identity hasher: salt/minLength/alphabet resolved from HashIdOptions at hasher-build time
    // by HashIdService.GetHasherFor (detects isIdentityHasher == true). No static reads.
    public AccessTreeHashIdConverter() : base(isIdentityHasher: true)
    {
    }
}