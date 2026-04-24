namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class CityHashIdConverter : JsonHashIdConverterAttribute<CityHashIdConverter>
{
    // Identity hasher: salt/minLength/alphabet resolved from HashIdOptions at hasher-build time
    // by HashIdService.GetHasherFor (detects isIdentityHasher == true). No static reads.
    public CityHashIdConverter() : base(isIdentityHasher: true)
    {
    }
}