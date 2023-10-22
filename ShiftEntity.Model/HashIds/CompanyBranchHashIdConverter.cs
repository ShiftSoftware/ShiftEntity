namespace ShiftSoftware.ShiftEntity.Model.HashIds;

internal class CompanyBranchHashIdConverter : JsonHashIdConverterAttribute<CompanyBranchHashIdConverter>
{
    public CompanyBranchHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
