namespace ShiftSoftware.ShiftEntity.Model.HashIds;

internal class CompanyHashIdConverter : JsonHashIdConverterAttribute<CompanyHashIdConverter>
{
    public CompanyHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
