namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class CompanyBranchHashIdConverter : JsonHashIdConverterAttribute<CompanyBranchHashIdConverter>
{
    // Identity hasher: salt/minLength/alphabet resolved from HashIdOptions at hasher-build time
    // by HashIdService.GetHasherFor (detects isIdentityHasher == true). No static reads.
    public CompanyBranchHashIdConverter() : base(isIdentityHasher: true)
    {
    }
}
