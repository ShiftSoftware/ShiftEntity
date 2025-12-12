using System.Linq;
using System.Text;
using System.Text.Json;

namespace ShiftSoftware.ShiftEntity.Core.Localization
{
    public class PropertyLocalizer
    {
        public static string? LocalizeProperty(string? value, string langKey)
        {
            string? localized = value;

            try
            {
                var json = JsonDocument.Parse(value!);

                var localizedText = json.RootElement.EnumerateObject()
                    .ToDictionary(k => k.Name, v => v.Value.GetString());

                if (localizedText.TryGetValue(langKey, out var localizedValue))
                {
                    localized = localizedValue;
                }

                else if (localizedText.TryGetValue("en", out var defaultValue))
                {
                    localized = defaultValue;
                }
                else
                {
                    localized = string.Empty;
                }
            }
            catch
            {
            }

            return localized;
        }
    }
}
