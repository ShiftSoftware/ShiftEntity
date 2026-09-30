using Newtonsoft.Json.Linq;
using System;
using System.Security.Cryptography;
using System.Text;

namespace ShiftSoftware.ShiftEntity.Core.Services;

public class TokenService
{
    static string ExpireDateTimeFormat = "yyyy-MM-dd.HH-mm-ss-ffff";
    public static (string token, string expires) GenerateSASToken(string uniqueTokenDescriptor, string id, DateTime expirationTime, string key)
    {
        if (string.IsNullOrWhiteSpace(uniqueTokenDescriptor))
            throw new ArgumentNullException(nameof(uniqueTokenDescriptor));

        // Use the invariant culture, so the text and the token do not depend on the thread's culture. Under th-TH, the
        // current culture's calendar would write 2026 as 2569, and the check reads the text as a Gregorian date.
        var expires = expirationTime.ToString(ExpireDateTimeFormat, System.Globalization.CultureInfo.InvariantCulture);

        var data = $"{uniqueTokenDescriptor}-{id}-{expires}";

        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key)))
        {
            var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));

            var token = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

            return (token, expires);
        }
    }

    public static (string token, string expires) GenerateSASToken(Type type, string id, DateTime expirationTime, string key)
    {
        return GenerateSASToken(type.FullName!, id, expirationTime, key);
    }

    public static bool ValidateSASToken(string uniqueTokenDescriptor, string id, string expires, string token, string key)
    {
        try
        {
            var expirationTime = DateTime.ParseExact(expires, ExpireDateTimeFormat, System.Globalization.CultureInfo.InvariantCulture);

            if (DateTime.UtcNow > expirationTime)
                return false;

            var newToken = TokenService.GenerateSASToken(uniqueTokenDescriptor, id, expirationTime, key).token;

            //A simple equality work. But to prevent against timing attack the below is more secure
            //return newToken.Equals(token);

            ReadOnlySpan<byte> newTokenBytes = Convert.FromBase64String(newToken);

            ReadOnlySpan<byte> providedTokenBytes = Convert.FromBase64String(token);

            return CryptographicOperations.FixedTimeEquals(newTokenBytes, providedTokenBytes);
        }
        catch
        {
            return false;
        }
    }

    public static bool ValidateSASToken(string storedToken, string providedToken) {

        ReadOnlySpan<byte> storedTokenBytes = Convert.FromBase64String(storedToken);
        ReadOnlySpan<byte> providedTokenBytes = Convert.FromBase64String(providedToken);

        return CryptographicOperations.FixedTimeEquals(storedTokenBytes, providedTokenBytes);
    }

    public static bool ValidateSASToken(Type type, string id, string expires, string token, string key)
    {
        return ValidateSASToken(type.FullName!, id, expires, token, key);
    }
}
