namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class CompanyHashIdConverter : JsonHashIdConverterAttribute<CompanyHashIdConverter>
{
    public CompanyHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
