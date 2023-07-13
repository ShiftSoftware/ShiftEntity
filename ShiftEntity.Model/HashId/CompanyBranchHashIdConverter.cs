namespace ShiftSoftware.ShiftEntity.Model.HashId;

internal class CompanyBranchHashIdConverter : JsonHashIdConverterAttribute<CompanyBranchHashIdConverter>
{
    public CompanyBranchHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
