namespace ShiftSoftware.ShiftEntity.Functions.AppCheck
{
    public class AntiBotOptions
    {
        public string HeaderKey { get; set; } = default!;
        public string AppCheckProjectNumber { get; set; } = default!;
        public string AppCheckServiceAccount { get; set; } = default!;

        public string HMSClientID { get; set; } = default!;
        public string HMSClientSecret { get; set; } = default!;
        public string HMSAppId { get; set; } = default!;
    }
}
