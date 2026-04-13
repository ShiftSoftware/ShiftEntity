using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Services.Attestation
{
    public interface IUnifiedAttestationService
    {
        /// <summary>
        /// Routes the attestation token to the appropriate verification service based on the client's platform.
        /// </summary>
        /// <param name="token">The attestation token provided by the client request mainly Firebase App Check Token for iOS and Android, HMS UserDetect Response Token for HMS/Huawei.</param>
        /// <param name="platform">The OS or attestation provider the token originated from.</param>
        /// <param name="withReplayProtection">
        /// If true, performs a strict, one-time-use network verification for Firebase App Check Token only to prevent replay attacks. 
        /// Defaults to false (which uses fast, offline JWT validation where supported).
        /// </param>
        /// <returns>True if the token is valid, trusted, otherwise, false.</returns>
        public ValueTask<bool> VerifyTokenAsync(string token, AttestationPlatform platform, bool? withReplayProtection = false);
    }
}
