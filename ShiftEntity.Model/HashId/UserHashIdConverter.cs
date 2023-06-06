namespace ShiftSoftware.ShiftEntity.Model.HashId;
internal class UserHashIdConverter : JsonHashIdConverterAttribute
{
    public UserHashIdConverter() : base(HashId.UserIdsSalt, HashId.UserIdsMinHashLength, HashId.UserIdsAlphabet,true)
    {
    }
}
