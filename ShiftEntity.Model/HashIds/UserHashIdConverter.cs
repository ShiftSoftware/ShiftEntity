namespace ShiftSoftware.ShiftEntity.Model.HashIds;
public class UserHashIdConverter : JsonHashIdConverterAttribute<UserHashIdConverter>
{
    // Identity hasher: salt/minLength/alphabet are resolved from HashIdOptions at hasher-build
    // time by HashIdService.GetHasherFor (which detects isIdentityHasher == true). No static reads.
    public UserHashIdConverter() : base(isIdentityHasher: true)
    {
    }
}
