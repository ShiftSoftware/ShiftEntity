namespace ShiftSoftware.ShiftEntity.Model.HashIds;
public class UserHashIdConverter : JsonHashIdConverterAttribute<UserHashIdConverter>
{
    public UserHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
