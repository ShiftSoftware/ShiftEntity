namespace ShiftSoftware.ShiftEntity.Functions.AppCheck
{
    public class AntiBotOptions
    {
        public string HeaderKey { get; set; } = default!;
        public string AppCheckProjectNumber { get; set; } = default!;
        public string AppCheckServiceAccount { get; set; } = default!;

        public string HMSAppId { get; set; } = default!;
        public string HMSIssuer { get; set; } = default!;
        public string HMSKeyId { get; set; } = default!;
        public string HMSPrivateKey { get; set; } = default!;
    }
}
