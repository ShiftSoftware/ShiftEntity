using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Services.Attestation
{
    public class FakeAttestationService : IUnifiedAttestationService
    {
        private readonly ILogger<FakeAttestationService> logger;
        public FakeAttestationService(ILogger<FakeAttestationService> logger)
        {
            this.logger = logger;
        }
        public ValueTask<bool> VerifyTokenAsync(string token, AttestationPlatform platform, bool? withReplayProtection = false)
        {
            // Warn the developer so they don't accidentally deploy this to production!
            logger.LogWarning("⚠️ DEVELOPMENT MODE ACTIVE: Bypassing actual {Platform} attestation verification.", platform);

            // Ensure the front-end is at least sending SOMETHING
            if (string.IsNullOrWhiteSpace(token))
            {
                logger.LogError("Attestation token is missing. Even in dev mode, a mock token must be provided.");
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(true);
        }
    }
}
