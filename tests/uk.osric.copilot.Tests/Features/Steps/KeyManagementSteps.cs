// SPDX-FileCopyrightText: Copyright (c) 2026 Osric Wilkinson
// SPDX-License-Identifier: MIT

namespace uk.osric.copilot.Tests.Features.Steps {
    using NUnit.Framework;
    using Reqnroll;
    using uk.osric.copilot.Models;
    using uk.osric.copilot.Services;
    using uk.osric.copilot.Tests.Helpers;

    [Binding]
    public class KeyManagementSteps : IAsyncDisposable {
        private TestDbContextFactory _factory = null!;
        private CertificateService _service = null!;

        private EmailCertificate? _lastCertificate;
        private List<EmailCertificate> _listedCertificates = [];

        [BeforeScenario]
        public async Task BeforeScenario() {
            _factory = new TestDbContextFactory();
            await _factory.InitialiseAsync();
            _service = new CertificateService(_factory);
        }

        [AfterScenario]
        public async Task AfterScenario() {
            await _factory.DisposeAsync();
        }

        // ── Given ────────────────────────────────────────────────────────────

        [Given(@"(\d+) certificates exist for ""(.*)""")]
        public async Task GivenNCertificatesExistFor(int count, string email) {
            for (var i = 0; i < count; i++)
                await _service.GenerateKeyPairAsync(email, 90);
        }

        [Given(@"a certificate exists for ""(.*)""")]
        public async Task GivenACertificateExistsFor(string email) {
            _lastCertificate = await _service.GenerateKeyPairAsync(email, 90);
        }

        [Given(@"a revoked certificate exists for ""(.*)""")]
        public async Task GivenARevokedCertificateExistsFor(string email) {
            var cert = await _service.GenerateKeyPairAsync(email, 90);
            await _service.RevokeCertificateAsync(cert.Id);
        }

        [Given(@"a valid certificate exists for ""(.*)""")]
        public async Task GivenAValidCertificateExistsFor(string email) {
            _lastCertificate = await _service.GenerateKeyPairAsync(email, 90);
        }

        // ── When ─────────────────────────────────────────────────────────────

        [When(@"I generate a certificate for ""(.*)"" with validity (\d+) days")]
        public async Task WhenIGenerateACertificateForWithValidityDays(string email, int days) {
            _lastCertificate = await _service.GenerateKeyPairAsync(email, days);
        }

        [When(@"I list certificates for ""(.*)""")]
        public async Task WhenIListCertificatesFor(string email) {
            _listedCertificates = await _service.GetCertificatesAsync(email);
        }

        [When(@"I download the certificate")]
        public void WhenIDownloadTheCertificate() {
            // PfxData is stored directly on the entity; no separate download call needed.
            Assert.That(_lastCertificate, Is.Not.Null);
        }

        [When(@"I revoke the certificate")]
        public async Task WhenIRevokeTheCertificate() {
            Assert.That(_lastCertificate, Is.Not.Null);
            await _service.RevokeCertificateAsync(_lastCertificate!.Id);
        }

        [When(@"I list active certificates for ""(.*)""")]
        public async Task WhenIListActiveCertificatesFor(string email) {
            _listedCertificates = await _service.GetActiveCertificatesAsync(email);
        }

        // ── Then ─────────────────────────────────────────────────────────────

        [Then(@"a certificate is returned with a valid serial number")]
        public void ThenACertificateIsReturnedWithAValidSerialNumber() {
            Assert.That(_lastCertificate, Is.Not.Null);
            Assert.That(_lastCertificate!.Fingerprint, Is.Not.Null.And.Not.Empty);
        }

        [Then(@"the certificate is not revoked")]
        public void ThenTheCertificateIsNotRevoked() {
            Assert.That(_lastCertificate!.IsRevoked, Is.False);
        }

        [Then(@"the certificate expires in approximately (\d+) days")]
        public void ThenTheCertificateExpiresInApproximatelyDays(int days) {
            var expected = _lastCertificate!.NotBefore.AddDays(days);
            Assert.That(_lastCertificate.NotAfter, Is.EqualTo(expected).Within(TimeSpan.FromMinutes(1)));
        }

        [Then(@"(\d+) certificates are returned")]
        public void ThenNCertificatesAreReturned(int count) {
            Assert.That(_listedCertificates, Has.Count.EqualTo(count));
        }

        [Then(@"I receive a non-empty PFX file")]
        public void ThenIReceiveANonEmptyPfxFile() {
            Assert.That(_lastCertificate!.PfxData, Is.Not.Null.And.Not.Empty);
        }

        [Then(@"the certificate is marked as revoked")]
        public async Task ThenTheCertificateIsMarkedAsRevoked() {
            var updated = await _service.GetCertificateByIdAsync(_lastCertificate!.Id);
            Assert.That(updated!.IsRevoked, Is.True);
        }

        [Then(@"active certificates for ""(.*)"" does not include the revoked certificate")]
        public async Task ThenActiveCertificatesForDoesNotIncludeTheRevokedCertificate(string email) {
            var active = await _service.GetActiveCertificatesAsync(email);
            Assert.That(active.All(c => !c.IsRevoked), Is.True);
        }

        [Then(@"only (\d+) certificate is returned")]
        public void ThenOnlyNCertificateIsReturned(int count) {
            Assert.That(_listedCertificates, Has.Count.EqualTo(count));
        }

        public async ValueTask DisposeAsync() {
            await _factory.DisposeAsync();
        }
    }
}
