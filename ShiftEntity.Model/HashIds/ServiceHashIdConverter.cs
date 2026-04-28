namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class ServiceHashIdConverter : JsonHashIdConverterAttribute<ServiceHashIdConverter>
{
    public ServiceHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
