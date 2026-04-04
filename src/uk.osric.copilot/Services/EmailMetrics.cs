// SPDX-FileCopyrightText: Copyright (c) 2026 Osric Wilkinson
// SPDX-License-Identifier: MIT

namespace uk.osric.copilot.Services {
    using System.Diagnostics.Metrics;

    public sealed class EmailMetrics : IDisposable {
        private readonly Meter _meter = new("uk.osric.copilot.email");

        internal Meter Meter => _meter;

        private readonly Counter<long> _received;
        private readonly Counter<long> _dropped;
        private readonly Counter<long> _replied;
        private readonly Counter<long> _processed;

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
        }

        public void RecordReceived() => _received.Add(1);

        public void RecordDropped(string outcome) =>
            _dropped.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

        public void RecordReplied(string outcome = "unknown_project") =>
            _replied.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

        public void RecordProcessed() => _processed.Add(1, new KeyValuePair<string, object?>("outcome", "success"));

        public void Dispose() => _meter.Dispose();
    }
}
