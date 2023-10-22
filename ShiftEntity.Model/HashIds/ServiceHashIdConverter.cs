namespace ShiftSoftware.ShiftEntity.Model.HashIds;

internal class ServiceHashIdConverter : JsonHashIdConverterAttribute<ServiceHashIdConverter>
{
    public ServiceHashIdConverter() : base(HashId.IdentityHashIdMinLength, HashId.IdentityHashIdSalt, HashId.IdentityHashIdAlphabet, true)
    {
    }
}
