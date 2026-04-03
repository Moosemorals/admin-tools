namespace uk.osric.copilot.Services {
    using System.Threading.Channels;
    using Microsoft.Extensions.Logging;

    /// <summary>A message to push to SSE subscribers.</summary>
    /// <param name="Id">
    /// Optional monotonic message ID (null for service-level events that are not
    /// persisted to the database, e.g. ServiceReady or SessionCreated).
    /// </param>
    /// <param name="Json">The JSON payload to emit as the SSE <c>data:</c> line.</param>
    public record SseMessage(long? Id, string Json);

    /// <summary>
    /// Thread-safe fan-out publisher for Server-Sent Event messages.
    /// Extracted from <see cref="CopilotService"/> so the SSE endpoint can
    /// subscribe without a circular dependency on the full service.
    /// </summary>
    public sealed class SseBroadcaster(ILogger<SseBroadcaster> logger) {
        private readonly List<Channel<SseMessage>> _subscribers = [];
        private readonly Lock _lock = new();

        /// <summary>
        /// Creates a new subscriber channel and returns its reader.
        /// The caller MUST call <see cref="Unsubscribe"/> when the connection closes.
        /// </summary>
        public ChannelReader<SseMessage> Subscribe() {
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

        /// <summary>Removes a subscriber (call when the SSE connection closes).</summary>
        public void Unsubscribe(ChannelReader<SseMessage> reader) {
            lock (_lock) {
                _subscribers.RemoveAll(c => c.Reader == reader);
            }
        }

        /// <summary>Pushes a message to every subscriber channel.</summary>
        internal void Broadcast(SseMessage message) {
            Channel<SseMessage>[] snapshot;
            lock (_lock) {
                snapshot = [.. _subscribers];
            }
            foreach (var channel in snapshot) {
                if (!channel.Writer.TryWrite(message)) {
                    logger.LogDebug("Dropped SSE event for a slow/closed subscriber.");
                }
            }
        }
    }
}
