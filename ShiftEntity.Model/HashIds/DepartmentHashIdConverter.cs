namespace ShiftSoftware.ShiftEntity.Model.HashIds;

internal class DepartmentHashIdConverter : JsonHashIdConverterAttribute<DepartmentHashIdConverter>
{
    public DepartmentHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
