// SPDX-FileCopyrightText: Copyright (c) 2026 Osric Wilkinson
// SPDX-License-Identifier: MIT

namespace uk.osric.copilot.Models {
    /// <summary>
    /// A single stored event message — either a Copilot SDK event, a user prompt,
    /// or a user-input request/reply. Gives the SSE feed a durable, replayable log.
    /// </summary>
    public class SessionMessage {
        /// <summary>Monotonically increasing surrogate key (SQLite AUTOINCREMENT).</summary>
        public long Id { get; set; }

        public string SessionId { get; set; } = null!;

        /// <summary>Value of the <c>_eventType</c> metadata field.</summary>
        public string EventType { get; set; } = null!;

        /// <summary>
        /// Full JSON payload of the event, including all <c>_</c>-prefixed metadata
        /// fields <em>except</em> <c>_id</c> (which is derived from <see cref="Id"/>
        /// at read time to avoid two-phase writes).
        /// </summary>
        public string Payload { get; set; } = null!;

        public DateTimeOffset CreatedAt { get; set; }
    }
}
