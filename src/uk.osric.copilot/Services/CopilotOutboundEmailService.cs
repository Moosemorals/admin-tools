namespace uk.osric.copilot.Services {
    using System.Text;
    using System.Text.Json;

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
                    bool hasData;
                    try {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                        hasData = await reader.WaitToReadAsync(timeoutCts.Token);
                    } catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) {
                        continue;
                    }
                    if (!hasData) {
                        break;
                    }
                    while (reader.TryRead(out var msg)) {
                        if (msg.Json is null) {
                            continue;
                        }
                        try {
                            using var doc = JsonDocument.Parse(msg.Json);
                            var root = doc.RootElement;
                            if (!root.TryGetProperty("_sessionId", out var sidProp)) {
                                continue;
                            }
                            var sessionId = sidProp.GetString();
                            if (sessionId is null) {
                                continue;
                            }
                            if (!root.TryGetProperty("_eventType", out var etProp)) {
                                continue;
                            }
                            var eventType = etProp.GetString();
                            if (eventType == "ProgressMessage" &&
                                root.TryGetProperty("text", out var textProp)) {
                                if (!buffers.ContainsKey(sessionId)) {
                                    buffers[sessionId] = new StringBuilder();
                                }
                                buffers[sessionId].Append(textProp.GetString());
                            }
                            var isTerminal = eventType?.EndsWith("Complete", StringComparison.Ordinal) == true
                                || eventType is "CompletedMessage";
                            if (isTerminal) {
                                var emailAddress = copilot.GetSessionEmailAddress(sessionId);
                                if (emailAddress is not null) {
                                    var responseText = buffers.TryGetValue(sessionId, out var sb) && sb.Length > 0
                                        ? sb.ToString()
                                        : "Request completed.";
                                    buffers.Remove(sessionId);
                                    _ = TrySendReplyAsync(emailAddress, responseText, stoppingToken);
                                } else {
                                    buffers.Remove(sessionId);
                                }
                            }
                        } catch (JsonException) { }
                    }
                }
            } finally {
                broadcaster.Unsubscribe(reader);
            }
        }

        private async Task TrySendReplyAsync(string emailAddress, string responseText, CancellationToken cancellationToken) {
            try {
                await smtp.SendReplyAsync(emailAddress, "Copilot response", responseText, cancellationToken);
            } catch (Exception ex) {
                logger.LogError(ex, "Failed to send Copilot response email to {Email}.", emailAddress);
            }
        }
    }
}
