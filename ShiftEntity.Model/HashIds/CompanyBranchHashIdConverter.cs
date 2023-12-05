namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class CompanyBranchHashIdConverter : JsonHashIdConverterAttribute<CompanyBranchHashIdConverter>
{
    public CompanyBranchHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
