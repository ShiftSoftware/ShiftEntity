namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class DepartmentHashIdConverter : JsonHashIdConverterAttribute<DepartmentHashIdConverter>
{
    // Identity hasher: salt/minLength/alphabet resolved from HashIdOptions at hasher-build time
    // by HashIdService.GetHasherFor (detects isIdentityHasher == true). No static reads.
    public DepartmentHashIdConverter() : base(isIdentityHasher: true)
    {
    }
}
