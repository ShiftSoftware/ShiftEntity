namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class CompanyHashIdConverter : JsonHashIdConverterAttribute<CompanyHashIdConverter>
{
    // Identity hasher: salt/minLength/alphabet resolved from HashIdOptions at hasher-build time
    // by HashIdService.GetHasherFor (detects isIdentityHasher == true). No static reads.
    public CompanyHashIdConverter() : base(isIdentityHasher: true)
    {
    }
}
