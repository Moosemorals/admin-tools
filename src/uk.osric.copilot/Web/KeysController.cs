namespace uk.osric.copilot.Web {
    using Microsoft.AspNetCore.Mvc;
    using uk.osric.copilot.Models;
    using uk.osric.copilot.Services;

    [ApiController]
    public sealed class KeysController(CertificateService certificates) : ControllerBase {

        /// <summary>Lists all certificates for the given email address.</summary>
        [HttpGet("/api/keys")]
        public async Task<IActionResult> ListKeys([FromQuery] string email) {
            if (string.IsNullOrWhiteSpace(email))
                return BadRequest("email is required.");

            var certs = await certificates.GetCertificatesAsync(email);
            return Ok(certs.Select(c => new {
                c.Id,
                c.EmailAddress,
                c.SubjectDn,
                c.Fingerprint,
                c.NotBefore,
                c.NotAfter,
                c.IsRevoked,
                c.CreatedAt
            }));
        }

        /// <summary>Generates a new key pair and self-signed certificate.</summary>
        [HttpPost("/api/keys/generate")]
        public async Task<IActionResult> GenerateKey([FromBody] GenerateKeyRequest body) {
            if (string.IsNullOrWhiteSpace(body.EmailAddress))
                return BadRequest("emailAddress is required.");

            var validDays = body.ValidDays > 0 ? body.ValidDays : 365;
            var cert = await certificates.GenerateKeyPairAsync(body.EmailAddress, validDays, body.KeyType);
            return Ok(new {
                cert.Id,
                cert.EmailAddress,
                cert.SubjectDn,
                cert.Fingerprint,
                cert.NotBefore,
                cert.NotAfter,
                cert.IsRevoked,
                cert.CreatedAt,
                downloadUrl = $"/api/keys/{cert.Id}/download"
            });
        }

        /// <summary>Downloads the PFX (PKCS#12) for the given certificate ID.</summary>
        [HttpGet("/api/keys/{id:int}/download")]
        public async Task<IActionResult> DownloadKey(int id) {
            var cert = await certificates.GetCertificateByIdAsync(id);
            if (cert is null) return NotFound();

            var filename = $"{cert.EmailAddress.Replace("@", "_at_").Replace(".", "_")}.pfx";
            return File(cert.PfxData, "application/x-pkcs12", filename);
        }

        /// <summary>Revokes a certificate (sets is_revoked; does NOT delete the row).</summary>
        [HttpDelete("/api/keys/{id:int}")]
        public async Task<IActionResult> RevokeKey(int id) {
            var cert = await certificates.GetCertificateByIdAsync(id);
            if (cert is null) return NotFound();

            await certificates.RevokeCertificateAsync(id);
            return NoContent();
        }
    }

    public sealed record GenerateKeyRequest(string EmailAddress, int ValidDays = 365, KeyType KeyType = KeyType.Ecdsa);
}
