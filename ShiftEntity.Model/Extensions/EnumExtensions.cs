using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace System;

public static class EnumExtensions
{
    public static string Describe(this Enum val)
    {
        return GetDescriptionString(val);
    }

    private static string GetDescriptionString<T>(T val)
    {
        var displayAttributes = (DisplayAttribute[]?)val?
           .GetType()
           .GetField(val.ToString())?
           .GetCustomAttributes(typeof(DisplayAttribute), false);

        if (displayAttributes?.Length > 0)
        {
            try
            {
                return displayAttributes[0].GetName();
            }
            catch
            {
                return displayAttributes[0].Name;
            }
        }

        var attributes = (DescriptionAttribute[]?)val?
           .GetType()
           .GetField(val.ToString())?
           .GetCustomAttributes(typeof(DescriptionAttribute), false);

        return attributes?.Length > 0 ? attributes[0].Description : val?.ToString() ?? "";
    }
}
