namespace uk.osric.copilot.Tests.Unit {
    using System.Security.Cryptography.X509Certificates;
    using NUnit.Framework;
    using uk.osric.copilot.Services;
    using uk.osric.copilot.Tests.Helpers;

    [TestFixture]
    public class CertificateServiceTests {
        private TestDbContextFactory _factory = null!;
        private CertificateService _sut = null!;

        [SetUp]
        public async Task SetUp() {
            _factory = new TestDbContextFactory();
            await _factory.InitialiseAsync();
            _sut = new CertificateService(_factory);
        }

        [TearDown]
        public async Task TearDown() {
            await _factory.DisposeAsync();
        }

        [Test]
        public async Task GenerateKeyPairAsync_CreatesEntityWithCorrectEmailAddress() {
            var cert = await _sut.GenerateKeyPairAsync("alice@example.com", 90);

            Assert.That(cert.EmailAddress, Is.EqualTo("alice@example.com"));
        }

        [Test]
        public async Task GenerateKeyPairAsync_CreatesEntityWithCorrectValidityWindow() {
            var before = DateTimeOffset.UtcNow;
            var cert = await _sut.GenerateKeyPairAsync("bob@example.com", 365);
            var after = DateTimeOffset.UtcNow;

            Assert.That(cert.NotBefore, Is.GreaterThanOrEqualTo(before).Within(TimeSpan.FromSeconds(5)));
            Assert.That(cert.NotAfter, Is.EqualTo(cert.NotBefore.AddDays(365)).Within(TimeSpan.FromSeconds(5)));
        }

        [Test]
        public async Task RevokeCertificateAsync_SetsIsRevokedTrue() {
            var cert = await _sut.GenerateKeyPairAsync("carol@example.com", 90);
            Assert.That(cert.IsRevoked, Is.False);

            await _sut.RevokeCertificateAsync(cert.Id);

            var updated = await _sut.GetCertificateByIdAsync(cert.Id);
            Assert.That(updated!.IsRevoked, Is.True);
        }

        [Test]
        public async Task GetActiveCertificatesAsync_ExcludesRevokedCertificates() {
            var cert = await _sut.GenerateKeyPairAsync("dan@example.com", 90);
            await _sut.RevokeCertificateAsync(cert.Id);

            var active = await _sut.GetActiveCertificatesAsync("dan@example.com");

            Assert.That(active, Is.Empty);
        }

        [Test]
        public async Task GetActiveCertificatesAsync_ExcludesExpiredCertificates() {
            // Generate a cert and then directly update its NotAfter to be in the past
            var cert = await _sut.GenerateKeyPairAsync("eve@example.com", 1);
            await using var db = _factory.CreateDbContext();
            var entity = await db.EmailCertificates.FindAsync(cert.Id);
            entity!.NotAfter = DateTimeOffset.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();

            var active = await _sut.GetActiveCertificatesAsync("eve@example.com");

            Assert.That(active, Is.Empty);
        }

        [Test]
        public async Task ValidateCertificateAsync_ReturnsNullForRevokedCertificate() {
            var cert = await _sut.GenerateKeyPairAsync("frank@example.com", 90);
            await _sut.RevokeCertificateAsync(cert.Id);

            using var x509 = X509CertificateLoader.LoadPkcs12(cert.PfxData, string.Empty);
            var result = await _sut.ValidateCertificateAsync(x509);

            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task ValidateCertificateAsync_ReturnsNullForUnknownCertificate() {
            // Generate a certificate locally without storing it in the DB
            using var ecdsa = System.Security.Cryptography.ECDsa.Create();
            var req = new CertificateRequest("CN=ghost@example.com", ecdsa, System.Security.Cryptography.HashAlgorithmName.SHA256);
            using var x509 = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));

            var result = await _sut.ValidateCertificateAsync(x509);

            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task ValidateCertificateAsync_ReturnsMatchingRecordForValidCertificate() {
            var cert = await _sut.GenerateKeyPairAsync("heidi@example.com", 90);

            using var x509 = X509CertificateLoader.LoadPkcs12(cert.PfxData, string.Empty);
            var result = await _sut.ValidateCertificateAsync(x509);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Id, Is.EqualTo(cert.Id));
        }
    }
}
