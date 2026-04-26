namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class TeamHashIdConverter : JsonHashIdConverterAttribute<TeamHashIdConverter>
{
    public TeamHashIdConverter() : base(configurationName: IdentityConfigurationName)
    {
    }
}
