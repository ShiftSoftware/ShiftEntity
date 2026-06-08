namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class ServiceHashIdConverter : JsonHashIdConverterAttribute<ServiceHashIdConverter>
{
    public ServiceHashIdConverter() : base(configurationName: IdentityConfigurationName)
    {
    }
}
