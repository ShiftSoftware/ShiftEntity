using System.Text.Json.Serialization;

namespace ShiftSoftware.ShiftEntity.Functions.AppCheck
{
    public class HMSUserDetectRequestBody
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = default!;
        [JsonPropertyName("response")]
        public string Response { get; set; } = default!;
    }
    public class HMSUserDetectResponseBody
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; } = default!;
        [JsonPropertyName("error-codes")]
        public string ErrorCodes { get; set; } = default!;
        [JsonPropertyName("challenge_ts")]
        public string? ChallengeTS { get; set; }
        [JsonPropertyName("apk_package_name")]
        public string? APKPackageName { get; set; }
    }
}
