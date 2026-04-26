namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class BrandHashIdConverter : JsonHashIdConverterAttribute<BrandHashIdConverter>
{
    public BrandHashIdConverter() : base(configurationName: IdentityConfigurationName)
    {
    }
}
