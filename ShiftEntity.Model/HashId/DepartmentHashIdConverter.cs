namespace ShiftSoftware.ShiftEntity.Model.HashId;

internal class DepartmentHashIdConverter : JsonHashIdConverterAttribute<DepartmentHashIdConverter>
{
    public DepartmentHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
