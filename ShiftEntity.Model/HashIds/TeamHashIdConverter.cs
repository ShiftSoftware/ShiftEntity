namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class TeamHashIdConverter : JsonHashIdConverterAttribute<TeamHashIdConverter>
{
    public TeamHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
