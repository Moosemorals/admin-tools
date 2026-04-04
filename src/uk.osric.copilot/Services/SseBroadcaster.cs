// SPDX-FileCopyrightText: Copyright (c) 2026 Osric Wilkinson
// SPDX-License-Identifier: MIT

namespace uk.osric.copilot.Services {
    using System.Threading.Channels;

    /// <summary>
    /// A message to push to SSE subscribers.
    /// </summary>
    /// <param name="Id">
    /// Monotonic message ID, or <c>null</c> for service-level events (e.g. ServiceReady,
    /// SessionCreated) that are not persisted to the database and do not need replay support.
    /// </param>
    /// <param name="Json">The JSON payload to emit as the SSE <c>data:</c> line.</param>
    internal sealed record SseMessage(long? Id, string Json);

    /// <summary>
    /// Thread-safe fan-out broadcaster for Server-Sent Event messages.
    /// Each SSE connection subscribes to receive a private channel reader; the broadcaster
    /// pushes every message to every connected subscriber concurrently.
    /// </summary>
    public sealed class SseBroadcaster(ILogger<SseBroadcaster> logger) {
        private readonly List<Channel<SseMessage>> _subscribers = [];
        private readonly Lock _lock = new();

        /// <summary>
        /// Creates a new subscriber channel and returns its reader.
        /// The caller MUST call <see cref="Unsubscribe"/> when the connection closes to
        /// prevent memory leaks.
        /// </summary>
        internal ChannelReader<SseMessage> Subscribe() {
            var channel = Channel.CreateUnbounded<SseMessage>(new UnboundedChannelOptions {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
            lock (_lock) {
                _subscribers.Add(channel);
            }
            return channel.Reader;
        }

        /// <summary>
        /// Removes and discards the subscriber channel associated with <paramref name="reader"/>.
        /// Call this in a <c>finally</c> block when the SSE connection closes.
        /// </summary>
        internal void Unsubscribe(ChannelReader<SseMessage> reader) {
            lock (_lock) {
                _subscribers.RemoveAll(c => c.Reader == reader);
            }
        }

        /// <summary>Pushes <paramref name="message"/> to every active subscriber channel.</summary>
        internal void Broadcast(SseMessage message) {
            Channel<SseMessage>[] snapshot;
            lock (_lock) {
                snapshot = [.. _subscribers];
            }
            foreach (var channel in snapshot) {
                if (!channel.Writer.TryWrite(message)) {
                    // Unbounded channels should never be full; a failed write means the
                    // subscriber has already been collected or is otherwise defunct.
                    logger.LogDebug("Dropped SSE event for a slow/closed subscriber.");
                }
            }
        }
    }
}
