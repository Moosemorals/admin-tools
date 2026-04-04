namespace uk.osric.copilot.Services {
    using System.Text;
    using System.Text.Json;

    /// <summary>
    /// Monitors Copilot SSE events for sessions that were initiated via email
    /// and sends reply emails when the session reaches a terminal state.
    /// </summary>
    public sealed class CopilotOutboundEmailService(
            SseBroadcaster broadcaster,
            CopilotService copilot,
            SmtpSenderService smtp,
            ILogger<CopilotOutboundEmailService> logger) : BackgroundService {

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            var reader = broadcaster.Subscribe();
            var buffers = new Dictionary<string, StringBuilder>();
            try {
                while (!stoppingToken.IsCancellationRequested) {
                    if (!await reader.WaitToReadAsync(stoppingToken)) {
                        break;
                    }
                    while (reader.TryRead(out var msg)) {
                        if (msg.Json is null) {
                            continue;
                        }
                        try {
                            ProcessEvent(msg.Json, buffers, stoppingToken);
                        } catch (JsonException) { }
                    }
                }
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                // normal shutdown
            } finally {
                broadcaster.Unsubscribe(reader);
            }
        }

        private void ProcessEvent(string json, Dictionary<string, StringBuilder> buffers, CancellationToken cancellationToken) {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("_sessionId", out var sidProp) || !(sidProp.GetString() is string sessionId)) {
                return;
            }
            if (!root.TryGetProperty("_eventType", out var etProp)) {
                return;
            }
            var eventType = etProp.GetString();
            if (eventType == "ProgressMessage" && root.TryGetProperty("text", out var textProp)) {
                buffers.TryAdd(sessionId, new StringBuilder());
                buffers[sessionId].Append(textProp.GetString());
            }
            if (IsTerminal(eventType)) {
                var emailAddress = copilot.GetSessionEmailAddress(sessionId);
                if (emailAddress is not null) {
                    var inReplyTo = copilot.GetSessionInboundMessageId(sessionId);
                    var responseText = buffers.TryGetValue(sessionId, out var sb) && sb.Length > 0
                        ? sb.ToString()
                        : "Request completed.";
                    buffers.Remove(sessionId);
                    _ = TrySendReplyAsync(emailAddress, responseText, inReplyTo, cancellationToken);
                } else {
                    buffers.Remove(sessionId);
                }
            }
        }

        private static bool IsTerminal(string? eventType) =>
            eventType?.EndsWith("Complete", StringComparison.Ordinal) == true
            || eventType is "CompletedMessage";

        private async Task TrySendReplyAsync(string emailAddress, string responseText, string? inReplyTo, CancellationToken cancellationToken) {
            try {
                await smtp.SendReplyAsync(emailAddress, "Copilot response", responseText, inReplyTo, cancellationToken: cancellationToken);
            } catch (Exception ex) {
                logger.LogError(ex, "Failed to send Copilot response email to {Email}.", emailAddress);
            }
        }
    }
}
