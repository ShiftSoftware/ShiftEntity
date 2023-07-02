namespace ShiftSoftware.ShiftEntity.Model.HashId;

internal class CompanyHashIdConverter : JsonHashIdConverterAttribute<CompanyHashIdConverter>
{
    public CompanyHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
