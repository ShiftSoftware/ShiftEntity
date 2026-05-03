namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class ServiceHashIdConverter : JsonHashIdConverterAttribute<ServiceHashIdConverter>
{
    // Identity hasher: salt/minLength/alphabet resolved from HashIdOptions at hasher-build time
    // by HashIdService.GetHasherFor (detects isIdentityHasher == true). No static reads.
    public ServiceHashIdConverter() : base(isIdentityHasher: true)
    {
    }
}
