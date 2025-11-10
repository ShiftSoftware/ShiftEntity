namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class AccessTreeHashIdConverter : JsonHashIdConverterAttribute<AccessTreeHashIdConverter>
{
    public AccessTreeHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}