namespace ShiftSoftware.ShiftEntity.Model.HashId;
public class UserHashIdConverter : JsonHashIdConverterAttribute<UserHashIdConverter>
{
    public UserHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
