namespace ShiftSoftware.ShiftEntity.Model.HashId;
internal class UserHashIdConverter : JsonHashIdConverterAttribute<UserHashIdConverter>
{
    public UserHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
