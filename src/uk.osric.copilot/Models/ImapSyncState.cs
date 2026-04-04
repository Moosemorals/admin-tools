// SPDX-FileCopyrightText: Copyright (c) 2026 Osric Wilkinson
// SPDX-License-Identifier: MIT

namespace uk.osric.copilot.Models {
    /// <summary>
    /// Persisted IMAP synchronisation state for the configured inbox.
    /// A single row (Id = 1) is maintained so that QRESYNC can efficiently
    /// resume after a restart without re-processing historical messages.
    /// </summary>
    public class ImapSyncState {
        /// <summary>Always 1 — single-row table for the configured IMAP account.</summary>
        public int Id { get; set; } = 1;

        /// <summary>IMAP UIDVALIDITY value (32-bit per RFC 3501); detects mailbox recreation.</summary>
        public uint UidValidity { get; set; }

        /// <summary>
        /// CONDSTORE/QRESYNC HIGHESTMODSEQ; allows efficient retrieval of
        /// changes since the last successful sync.
        /// </summary>
        public ulong HighestModSeq { get; set; }

        /// <summary>Highest message UID successfully enqueued for processing.</summary>
        public uint LastSeenUid { get; set; }
    }
}
