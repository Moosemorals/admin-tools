// SPDX-FileCopyrightText: Copyright (c) 2026 Osric Wilkinson
// SPDX-License-Identifier: MIT

namespace uk.osric.copilot.Services {
    using System.Diagnostics.Metrics;

    public sealed class CopilotMetrics : IDisposable {
        private readonly Meter _meter = new("uk.osric.copilot");

        private readonly UpDownCounter<long> _sessionsTotal;
        private readonly UpDownCounter<long> _sessionsActive;
        private readonly Histogram<double> _requestDuration;
        private Func<int>? _projectCountCallback;

        public CopilotMetrics() {
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

        public void IncrementSessionsTotal() => _sessionsTotal.Add(1);
        public void IncrementSessionsActive() => _sessionsActive.Add(1);
        public void DecrementSessionsActive() => _sessionsActive.Add(-1);
        public void RecordRequestDuration(double seconds) => _requestDuration.Record(seconds);
        public void SetProjectCountCallback(Func<int> callback) => _projectCountCallback = callback;

        public void Dispose() => _meter.Dispose();
    }
}
