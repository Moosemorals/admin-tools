namespace uk.osric.copilot.Tests.Unit {
    using System.Diagnostics.Metrics;
    using NUnit.Framework;
    using uk.osric.copilot.Services;

    [TestFixture]
    public class EmailMetricsTests {
        private List<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> _measurements = null!;
        private MeterListener _listener = null!;
        private EmailMetrics _sut = null!;

        [SetUp]
        public void SetUp() {
            _measurements = [];
            _sut = new EmailMetrics();

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

        [TearDown]
        public void TearDown() {
            _listener.Dispose();
            _sut.Dispose();
        }

        [Test]
        public void RecordReceived_IncrementsReceivedCounter() {
            _sut.RecordReceived();

            var m = _measurements.Single(x => x.Name == "email.messages.received");
            Assert.That(m.Value, Is.EqualTo(1));
        }

        [Test]
        public void RecordDropped_Unsigned_IncrementsDroppedCounter() {
            _sut.RecordDropped("unsigned");

            var m = AssertDroppedWith("unsigned");
            Assert.That(m.Value, Is.EqualTo(1));
        }

        [Test]
        public void RecordDropped_InvalidSignature_IncrementsDroppedCounterWithCorrectTag() {
            _sut.RecordDropped("invalid_signature");

            AssertDroppedWith("invalid_signature");
        }

        [Test]
        public void RecordDropped_UnknownSignature_IncrementsDroppedCounter() {
            _sut.RecordDropped("unknown_signature");

            AssertDroppedWith("unknown_signature");
        }

        [Test]
        public void RecordDropped_ExpiredCertificate_IncrementsDroppedCounter() {
            _sut.RecordDropped("expired_certificate");

            AssertDroppedWith("expired_certificate");
        }

        [Test]
        public void RecordDropped_RevokedCertificate_IncrementsDroppedCounter() {
            _sut.RecordDropped("revoked_certificate");

            AssertDroppedWith("revoked_certificate");
        }

        [Test]
        public void RecordReplied_IncrementsRepliedCounterWithUnknownProjectOutcome() {
            _sut.RecordReplied("unknown_project");

            var m = _measurements.Single(x => x.Name == "email.messages.replied");
            Assert.That(m.Value, Is.EqualTo(1));
            Assert.That(m.Tags, Has.One.Matches<KeyValuePair<string, object?>>(
                t => t.Key == "outcome" && (string?)t.Value == "unknown_project"));
        }

        [Test]
        public void RecordProcessed_IncrementsProcessedCounter() {
            _sut.RecordProcessed();

            var m = _measurements.Single(x => x.Name == "email.messages.processed");
            Assert.That(m.Value, Is.EqualTo(1));
        }

        private (string Name, long Value, KeyValuePair<string, object?>[] Tags) AssertDroppedWith(string outcome) {
            var m = _measurements.Single(x =>
                x.Name == "email.messages.dropped" &&
                x.Tags.Any(t => t.Key == "outcome" && (string?)t.Value == outcome));
            Assert.That(m.Value, Is.EqualTo(1));
            return m;
        }
    }
}
