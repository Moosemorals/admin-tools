namespace uk.osric.copilot.Services {
    using System.Threading.Channels;
    using MailKit;
    using MailKit.Net.Imap;
    using MailKit.Security;
    using Microsoft.Extensions.Options;
    using MimeKit;
    using uk.osric.copilot.Configuration;

    /// <summary>
    /// Background service that connects to the configured IMAP server, issues IDLE,
    /// and enqueues new messages to a shared <see cref="Channel{MimeMessage}"/>.
    /// </summary>
    public sealed class ImapListenerService(
            IOptions<CopilotOptions> options,
            ChannelWriter<MimeMessage> messageChannel,
            ILogger<ImapListenerService> logger) : BackgroundService {

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            var imap = options.Value.Email.Imap;
            if (string.IsNullOrWhiteSpace(imap.Host)) {
                logger.LogInformation("IMAP not configured; email listener disabled.");
                return;
            }

            while (!stoppingToken.IsCancellationRequested) {
                try {
                    await RunImapLoopAsync(imap, stoppingToken);
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    break;
                } catch (Exception ex) {
                    logger.LogError(ex, "IMAP connection error; retrying in 30 seconds.");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
        }

        private async Task RunImapLoopAsync(ImapOptions imap, CancellationToken stoppingToken) {
            using var client = new ImapClient();
            await client.ConnectAsync(imap.Host, imap.Port, GetImapSocketOptions(imap), stoppingToken);
            await client.AuthenticateAsync(imap.Username, imap.Password, stoppingToken);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, stoppingToken);

            logger.LogInformation("IMAP connected to {Host}. INBOX has {Count} messages.", imap.Host, client.Inbox.Count);

            var lastCount = client.Inbox.Count;

            while (!stoppingToken.IsCancellationRequested) {
                if (client.Capabilities.HasFlag(ImapCapabilities.Idle)) {
                    using var done = new CancellationTokenSource(TimeSpan.FromMinutes(9));
                    void onCountChanged(object? sender, EventArgs e) => done.Cancel();
                    client.Inbox.CountChanged += onCountChanged;
                    try {
                        await client.IdleAsync(done.Token, stoppingToken);
                    } catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) {
                        // Count changed or idle timed out — both are normal
                    } finally {
                        client.Inbox.CountChanged -= onCountChanged;
                    }
                } else {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    await client.NoOpAsync(stoppingToken);
                }

                var currentCount = client.Inbox.Count;
                for (var i = lastCount; i < currentCount; i++) {
                    try {
                        var message = await client.Inbox.GetMessageAsync(i, stoppingToken);
                        await messageChannel.WriteAsync(message, stoppingToken);
                        logger.LogDebug("Enqueued INBOX message at index {Index}.", i);
                    } catch (Exception ex) when (!stoppingToken.IsCancellationRequested) {
                        logger.LogWarning(ex, "Failed to fetch message at index {Index}.", i);
                    }
                }

                lastCount = currentCount;
            }

            await client.DisconnectAsync(quit: true, stoppingToken);
        }

        private static SecureSocketOptions GetImapSocketOptions(ImapOptions imap) {
            if (imap.Tls == "Always") return SecureSocketOptions.SslOnConnect;
            if (imap.Tls == "StartTls") return SecureSocketOptions.StartTls;
            return imap.Port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        }
    }
}
