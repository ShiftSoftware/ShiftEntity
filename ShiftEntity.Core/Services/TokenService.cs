using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Services;

public class TokenService
{
    public static (string token, string expires) GenerateSASToken(string uniqueType, string id, DateTime expirationTime, string key)
    {
        if (string.IsNullOrWhiteSpace(uniqueType))
            throw new ArgumentNullException(nameof(uniqueType));

        var expires = expirationTime.ToString("yyyy-MM-dd.HH-mm-ss-ffff");

        var data = $"{uniqueType}-{id}-{expires}";

        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key)))
        {
            var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));

            var token = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();

            return (token, expires);
        }
    }

    public static (string token, string expires) GenerateSASToken(Type type, string id, DateTime expirationTime, string key)
    {
        return GenerateSASToken(type.FullName!, id, expirationTime, key);
    }

    public static bool ValidateSASToken(string uniqueType, string id, string expires, string token, string key)
    {
        try
        {
            var expirationTime = DateTime.ParseExact(expires, "yyyy-MM-dd.HH-mm-ss-ffff", System.Globalization.CultureInfo.InvariantCulture);

            if (DateTime.UtcNow > expirationTime)
                return false;

            var newToken = TokenService.GenerateSASToken(uniqueType, id, expirationTime, key).token;

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

    public static bool ValidateSASToken(Type type, string id, string expires, string token, string key)
    {
        return ValidateSASToken(type.FullName!, id, expires, token, key);
    }
}
