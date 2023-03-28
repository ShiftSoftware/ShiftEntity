using System.ComponentModel;

namespace ShiftSoftware.ShiftEntity.Model.Extensions;

public static class EnumExtensions
{
    public static string Describe(this Enum val)
    {
        return GetDescriptionString(val);
    }

    private static string GetDescriptionString<T>(T val)
    {
        var attributes = (DescriptionAttribute[])val!
           .GetType()
           .GetField(val.ToString()!)!
           .GetCustomAttributes(typeof(DescriptionAttribute), false);

        return attributes.Length > 0 ? attributes[0].Description : string.Empty;
    }
}
