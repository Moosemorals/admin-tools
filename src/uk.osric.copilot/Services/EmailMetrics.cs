namespace uk.osric.copilot.Services {
    using System.Diagnostics.Metrics;

    public sealed class EmailMetrics : IDisposable {
        private readonly Meter _meter = new("uk.osric.copilot.email");

        private readonly Counter<long> _received;
        private readonly Counter<long> _dropped;
        private readonly Counter<long> _replied;
        private readonly Counter<long> _processed;

        private readonly UpDownCounter<long> _sessionsTotal;
        private readonly UpDownCounter<long> _sessionsActive;

        private readonly Histogram<double> _requestDuration;

        private Func<int>? _projectCountCallback;

        public EmailMetrics() {
            _received = _meter.CreateCounter<long>(
                "email.messages.received",
                description: "Total inbound email messages received");

            _dropped = _meter.CreateCounter<long>(
                "email.messages.dropped",
                description: "Total inbound email messages dropped");

            _replied = _meter.CreateCounter<long>(
                "email.messages.replied",
                description: "Total email replies sent");

            _processed = _meter.CreateCounter<long>(
                "email.messages.processed",
                description: "Total inbound email messages processed successfully");

            _sessionsTotal = _meter.CreateUpDownCounter<long>(
                "copilot.sessions.total",
                description: "Total number of sessions created");

            _sessionsActive = _meter.CreateUpDownCounter<long>(
                "copilot.sessions.active",
                description: "Number of currently active sessions");

            _requestDuration = _meter.CreateHistogram<double>(
                "copilot.request.duration",
                unit: "s",
                description: "Request processing duration",
                advice: new InstrumentAdvice<double> {
                    HistogramBucketBoundaries = [0.5, 1, 2, 5, 10, 30, 60, 120]
                });

            _meter.CreateObservableGauge(
                "copilot.projects.known",
                () => _projectCountCallback?.Invoke() ?? 0,
                description: "Number of known project folders");
        }

        public void RecordReceived() => _received.Add(1);

        public void RecordDropped(string outcome) =>
            _dropped.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

        public void RecordReplied(string outcome = "unknown_project") =>
            _replied.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

        public void RecordProcessed() => _processed.Add(1);

        public void IncrementSessionsTotal() => _sessionsTotal.Add(1);
        public void IncrementSessionsActive() => _sessionsActive.Add(1);
        public void DecrementSessionsActive() => _sessionsActive.Add(-1);

        public void RecordRequestDuration(double seconds) => _requestDuration.Record(seconds);

        public void SetProjectCountCallback(Func<int> callback) =>
            _projectCountCallback = callback;

        public void Dispose() => _meter.Dispose();
    }
}
