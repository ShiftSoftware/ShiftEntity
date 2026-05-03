namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class CityHashIdConverter : JsonHashIdConverterAttribute<CityHashIdConverter>
{
    public CityHashIdConverter() : base(configurationName: IdentityConfigurationName)
    {
    }
}