namespace ShiftSoftware.ShiftEntity.Model.HashIds;
public class UserHashIdConverter : JsonHashIdConverterAttribute<UserHashIdConverter>
{
    public UserHashIdConverter() : base(configurationName: IdentityConfigurationName)
    {
    }
}
