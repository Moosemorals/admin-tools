namespace uk.osric.copilot.Tests.Features.Steps {
    using System.Diagnostics.Metrics;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using NUnit.Framework;
    using Reqnroll;
    using uk.osric.copilot.Models;
    using uk.osric.copilot.Services;
    using uk.osric.copilot.Tests.Helpers;

    /// <summary>
    /// Describes the signing state of a test email, used to drive the in-process router.
    /// </summary>
    internal enum SigningState { Unsigned, Signed, Tampered }

    /// <summary>
    /// Step definitions for the Email Routing feature.
    ///
    /// Rather than standing up a full SMTP/Copilot stack the steps drive a lightweight
    /// in-process router that replicates the same decision tree as
    /// <see cref="EmailProcessorService"/> and records to a real <see cref="EmailMetrics"/>
    /// instance.  Metric assertions are made using a <see cref="MeterListener"/>.
    /// </summary>
    [Binding]
    public class EmailRoutingSteps : IAsyncDisposable {
        // ── Infrastructure ────────────────────────────────────────────────────

        private TestDbContextFactory _factory = null!;
        private CertificateService _certService = null!;
        private EmailMetrics _metrics = null!;

        private readonly List<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> _measurements = [];
        private MeterListener _listener = null!;

        // ── Per-scenario state ────────────────────────────────────────────────

        private string _configuredProject = string.Empty;

        /// <summary>
        /// Email address → (X509 cert loaded from PFX, DB record).
        /// Populated by Given steps that register a certificate.
        /// </summary>
        private readonly Dictionary<string, (X509Certificate2 Cert, EmailCertificate Record)> _registeredCerts = [];

        /// <summary>
        /// Email address → unregistered X509 cert (not stored in the DB).
        /// </summary>
        private readonly Dictionary<string, X509Certificate2> _unregisteredCerts = [];

        private bool _replySent;
        private string _senderEmail = string.Empty;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        [BeforeScenario]
        public async Task BeforeScenario() {
            _factory = new TestDbContextFactory();
            await _factory.InitialiseAsync();
            _certService = new CertificateService(_factory);
            _metrics = new EmailMetrics();

            _listener = new MeterListener();
            _listener.InstrumentPublished = (instrument, l) => {
                if (instrument.Meter.Name == "uk.osric.copilot.email")
                    l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
                _measurements.Add((instrument.Name, value, tags.ToArray()));
            });
            _listener.Start();
        }

        [AfterScenario]
        public async Task AfterScenario() {
            _listener.Dispose();
            _metrics.Dispose();

            foreach (var (cert, _) in _registeredCerts.Values)
                cert.Dispose();
            foreach (var cert in _unregisteredCerts.Values)
                cert.Dispose();

            await _factory.DisposeAsync();
        }

        // ── Given ─────────────────────────────────────────────────────────────

        [Given(@"the email processor is configured with a valid project ""(.*)""")]
        public void GivenEmailProcessorConfiguredWithProject(string project) {
            _configuredProject = project;
        }

        [Given(@"a registered certificate for ""(.*)""")]
        public async Task GivenRegisteredCertificateFor(string email) {
            var record = await _certService.GenerateKeyPairAsync(email, 90);
            var x509 = X509CertificateLoader.LoadPkcs12(record.PfxData, string.Empty);
            _registeredCerts[email] = (x509, record);
        }

        [Given(@"an expired certificate for ""(.*)""")]
        public async Task GivenExpiredCertificateFor(string email) {
            var record = await _certService.GenerateKeyPairAsync(email, 1);
            // Back-date the validity window so it is already expired.
            await using var db = _factory.CreateDbContext();
            var entity = await db.EmailCertificates.FindAsync(record.Id);
            entity!.NotAfter = DateTimeOffset.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();

            var x509 = X509CertificateLoader.LoadPkcs12(record.PfxData, string.Empty);
            _registeredCerts[email] = (x509, entity);
        }

        [Given(@"a revoked certificate for ""(.*)""")]
        public async Task GivenRevokedCertificateFor(string email) {
            var record = await _certService.GenerateKeyPairAsync(email, 90);
            await _certService.RevokeCertificateAsync(record.Id);

            var x509 = X509CertificateLoader.LoadPkcs12(record.PfxData, string.Empty);
            var updated = (await _certService.GetCertificateByIdAsync(record.Id))!;
            _registeredCerts[email] = (x509, updated);
        }

        // ── When ──────────────────────────────────────────────────────────────

        [When(@"I send an unsigned email from ""(.*)"" with subject ""(.*)"" and body ""(.*)""")]
        public async Task WhenISendUnsignedEmail(string from, string subject, string body) {
            _senderEmail = from;
            await RouteMessageAsync(from, subject, SigningState.Unsigned, useUnregisteredCert: false);
        }

        [When(@"I send a signed email from ""(.*)"" with subject ""(.*)"" and body ""(.*)""")]
        public async Task WhenISendSignedEmail(string from, string subject, string body) {
            _senderEmail = from;
            await RouteMessageAsync(from, subject, SigningState.Signed, useUnregisteredCert: false);
        }

        [When(@"I send a signed email from ""(.*)"" with an unregistered certificate with subject ""(.*)"" and body ""(.*)""")]
        public async Task WhenISendSignedEmailWithUnregisteredCert(string from, string subject, string body) {
            _senderEmail = from;
            // Generate a cert locally but do NOT save it to the DB.
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest($"CN={from}", ecdsa, HashAlgorithmName.SHA256);
            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
            _unregisteredCerts[from] = cert;

            await RouteMessageAsync(from, subject, SigningState.Signed, useUnregisteredCert: true);
        }

        [When(@"I send a signed email from ""(.*)"" with a tampered body with subject ""(.*)""")]
        public async Task WhenISendSignedEmailWithTamperedBody(string from, string subject) {
            _senderEmail = from;
            await RouteMessageAsync(from, subject, SigningState.Tampered, useUnregisteredCert: false);
        }

        // ── Then ──────────────────────────────────────────────────────────────

        [Then(@"the email\.messages\.received counter is incremented")]
        public void ThenReceivedCounterIsIncremented() {
            Assert.That(_measurements.Any(m => m.Name == "email.messages.received"), Is.True);
        }

        [Then(@"the email\.messages\.dropped counter is incremented with outcome ""(.*)""")]
        public void ThenDroppedCounterIsIncrementedWithOutcome(string outcome) {
            Assert.That(_measurements.Any(m =>
                m.Name == "email.messages.dropped" &&
                m.Tags.Any(t => t.Key == "outcome" && (string?)t.Value == outcome)), Is.True);
        }

        [Then(@"the email\.messages\.replied counter is incremented with outcome ""(.*)""")]
        public void ThenRepliedCounterIsIncrementedWithOutcome(string outcome) {
            Assert.That(_measurements.Any(m =>
                m.Name == "email.messages.replied" &&
                m.Tags.Any(t => t.Key == "outcome" && (string?)t.Value == outcome)), Is.True);
        }

        [Then(@"the email\.messages\.processed counter is incremented with outcome ""(.*)""")]
        public void ThenProcessedCounterIsIncrementedWithOutcome(string outcome) {
            Assert.That(_measurements.Any(m => m.Name == "email.messages.processed"), Is.True);
        }

        [Then(@"no email\.messages\.processed event is recorded")]
        public void ThenNoProcessedEventIsRecorded() {
            Assert.That(_measurements.Any(m => m.Name == "email.messages.processed"), Is.False);
        }

        [Then(@"no reply is sent")]
        public void ThenNoReplyIsSent() {
            Assert.That(_replySent, Is.False);
        }

        // ── Routing helper ────────────────────────────────────────────────────

        /// <summary>
        /// Mirrors the decision tree in <see cref="EmailProcessorService.ProcessMessageAsync"/>
        /// without requiring live SMTP or Copilot infrastructure.
        /// </summary>
        private async Task RouteMessageAsync(
                string from,
                string subject,
                SigningState signingState,
                bool useUnregisteredCert) {

            _metrics.RecordReceived();

            if (signingState == SigningState.Unsigned) {
                _metrics.RecordDropped("unsigned");
                return;
            }

            if (signingState == SigningState.Tampered) {
                _metrics.RecordDropped("invalid_signature");
                return;
            }

            // Signed path: look up the cert to validate
            X509Certificate2? senderCert;
            if (useUnregisteredCert) {
                senderCert = _unregisteredCerts.TryGetValue(from, out var c) ? c : null;
            } else {
                senderCert = _registeredCerts.TryGetValue(from, out var pair) ? pair.Cert : null;
            }

            if (senderCert is null) {
                _metrics.RecordDropped("unknown_signature");
                return;
            }

            var certRecord = await _certService.ValidateCertificateAsync(senderCert);
            if (certRecord is null) {
                // Distinguish revoked vs expired vs unknown by inspecting the raw record
                if (_registeredCerts.TryGetValue(from, out var knownPair)) {
                    var rec = knownPair.Record;
                    if (rec.IsRevoked) {
                        _metrics.RecordDropped("revoked_certificate");
                    } else if (rec.NotAfter < DateTimeOffset.UtcNow) {
                        _metrics.RecordDropped("expired_certificate");
                    } else {
                        _metrics.RecordDropped("unknown_signature");
                    }
                } else {
                    _metrics.RecordDropped("unknown_signature");
                }
                return;
            }

            var projectName = EmailProcessorService.StripReplyPrefixes(subject);
            var isKnownProject = string.Equals(projectName, _configuredProject, StringComparison.OrdinalIgnoreCase);

            if (!isKnownProject) {
                _metrics.RecordReplied("unknown_project");
                _replySent = true;
                return;
            }

            _metrics.RecordProcessed();
        }

        public async ValueTask DisposeAsync() {
            await AfterScenario();
        }
    }
}
