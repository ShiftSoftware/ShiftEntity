namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class UserGroupHashIdConverter : JsonHashIdConverterAttribute<UserGroupHashIdConverter>
{
    public UserGroupHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
