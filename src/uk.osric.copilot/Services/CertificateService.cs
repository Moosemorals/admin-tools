namespace uk.osric.copilot.Services {
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using Microsoft.EntityFrameworkCore;
    using uk.osric.copilot.Data;
    using uk.osric.copilot.Models;

    public sealed class CertificateService(IDbContextFactory<CopilotDbContext> dbFactory) {

        public async Task<EmailCertificate> GenerateKeyPairAsync(string emailAddress, int validDays, KeyType keyType = KeyType.Ecdsa) {
            AsymmetricAlgorithm key = keyType == KeyType.Rsa
                ? RSA.Create(2048)
                : (AsymmetricAlgorithm)ECDsa.Create(ECCurve.NamedCurves.nistP256);

            using var disposableKey = key;

            CertificateRequest req;
            if (key is RSA rsa) {
                req = new CertificateRequest(
                    $"CN={emailAddress}, E={emailAddress}",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
            } else {
                req = new CertificateRequest(
                    $"CN={emailAddress}, E={emailAddress}",
                    (ECDsa)key,
                    HashAlgorithmName.SHA256);
            }

            var san = new SubjectAlternativeNameBuilder();
            san.AddEmailAddress(emailAddress);
            req.CertificateExtensions.Add(san.Build());

            req.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation,
                    critical: true));

            req.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.4") },
                    critical: false));

            var notBefore = DateTimeOffset.UtcNow;
            var notAfter = notBefore.AddDays(validDays);

            using var cert = req.CreateSelfSigned(notBefore, notAfter);

            var pfxData = cert.Export(X509ContentType.Pfx, string.Empty);
            var derData = cert.Export(X509ContentType.Cert);

            var entity = new EmailCertificate {
                EmailAddress = emailAddress,
                SubjectDn = cert.Subject,
                Fingerprint = cert.GetCertHashString(HashAlgorithmName.SHA256),
                PfxData = pfxData,
                CertificateDer = derData,
                NotBefore = notBefore,
                NotAfter = notAfter,
                IsRevoked = false,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await using var db = await dbFactory.CreateDbContextAsync();
            db.EmailCertificates.Add(entity);
            await db.SaveChangesAsync();
            return entity;
        }

        public async Task<List<EmailCertificate>> GetCertificatesAsync(string emailAddress) {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.EmailCertificates
                .Where(c => c.EmailAddress == emailAddress)
                .OrderByDescending(c => c.NotBefore)
                .ToListAsync();
        }

        public async Task<List<EmailCertificate>> GetActiveCertificatesAsync(string emailAddress) {
            await using var db = await dbFactory.CreateDbContextAsync();
            var all = await db.EmailCertificates
                .Where(c => c.EmailAddress == emailAddress && !c.IsRevoked)
                .ToListAsync();
            var now = DateTimeOffset.UtcNow;
            return all.Where(c => c.NotBefore <= now && c.NotAfter >= now)
                      .OrderByDescending(c => c.NotBefore)
                      .ToList();
        }

        public async Task RevokeCertificateAsync(int id) {
            await using var db = await dbFactory.CreateDbContextAsync();
            var cert = await db.EmailCertificates.FindAsync(id);
            if (cert is not null) {
                cert.IsRevoked = true;
                await db.SaveChangesAsync();
            }
        }

        public async Task<EmailCertificate?> GetCertificateByIdAsync(int id) {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.EmailCertificates.FindAsync(id);
        }

        /// <summary>
        /// Validates that <paramref name="cert"/> matches a non-revoked, in-validity-period
        /// record in the database. Returns the matching record or null if invalid.
        /// </summary>
        public async Task<EmailCertificate?> ValidateCertificateAsync(X509Certificate2 cert) {
            var fingerprint = cert.GetCertHashString(HashAlgorithmName.SHA256);
            await using var db = await dbFactory.CreateDbContextAsync();
            var candidates = await db.EmailCertificates
                .Where(c => c.Fingerprint == fingerprint && !c.IsRevoked)
                .ToListAsync();

            var now = DateTimeOffset.UtcNow;
            return candidates.FirstOrDefault(c => {
                if (c.NotBefore > now || c.NotAfter < now) return false;
                using var stored = X509CertificateLoader.LoadCertificate(c.CertificateDer);
                return stored.RawData.AsSpan().SequenceEqual(cert.RawData);
            });
        }
    }
}
