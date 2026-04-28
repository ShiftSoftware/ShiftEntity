namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class DepartmentHashIdConverter : JsonHashIdConverterAttribute<DepartmentHashIdConverter>
{
    public DepartmentHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
