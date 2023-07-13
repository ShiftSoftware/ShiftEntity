namespace ShiftSoftware.ShiftEntity.Model.HashId;

internal class ServiceHashIdConverter : JsonHashIdConverterAttribute<ServiceHashIdConverter>
{
    public ServiceHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
