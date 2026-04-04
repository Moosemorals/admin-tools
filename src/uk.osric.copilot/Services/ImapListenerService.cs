namespace uk.osric.copilot.Services {
    using System.Threading.Channels;
    using MailKit;
    using MailKit.Net.Imap;
    using MailKit.Search;
    using MailKit.Security;
    using Microsoft.Extensions.Options;
    using MimeKit;
    using uk.osric.copilot.Configuration;

    public sealed class ImapListenerService(
            IOptions<CopilotOptions> options,
            ChannelWriter<MimeMessage> messageChannel,
            ILogger<ImapListenerService> logger) : BackgroundService {

        private uint _lastSeenUid;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            var imap = options.Value.Email.Imap;
            if (string.IsNullOrWhiteSpace(imap.Host)) {
                logger.LogInformation("IMAP not configured; email listener disabled.");
                return;
            }

            while (!stoppingToken.IsCancellationRequested) {
                try {
                    await RunImapLoopAsync(imap, stoppingToken);
                } catch (InvalidOperationException ex) when (ex.Message.Contains("IDLE")) {
                    logger.LogError(ex, "IMAP server does not support IDLE; email subsystem permanently disabled.");
                    return;
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    break;
                } catch (Exception ex) {
                    logger.LogError(ex, "IMAP connection error; retrying in 30 seconds.");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
        }

        private async Task RunImapLoopAsync(ImapOptions imap, CancellationToken stoppingToken) {
            var ourAddress = options.Value.Email.FromAddress;
            var idleTimeoutMinutes = imap.IdleTimeoutMinutes > 0 ? imap.IdleTimeoutMinutes : 27;

            using var client = new ImapClient();
            await client.ConnectAsync(imap.Host, imap.Port, GetImapSocketOptions(imap), stoppingToken);
            await client.AuthenticateAsync(imap.Username, imap.Password, stoppingToken);

            // Check IDLE support as early as possible — gate the whole subsystem on it
            if (!client.Capabilities.HasFlag(ImapCapabilities.Idle)) {
                throw new InvalidOperationException("IMAP server does not support IDLE. A server with IDLE support is required.");
            }

            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, stoppingToken);
            logger.LogInformation("IMAP connected to {Host}.", imap.Host);

            // Establish baseline: don't re-process existing messages on startup
            if (_lastSeenUid == 0) {
                var existingUids = await client.Inbox.SearchAsync(SearchQuery.All, stoppingToken);
                _lastSeenUid = existingUids.Count > 0 ? existingUids[existingUids.Count - 1].Id : 0;
                logger.LogDebug("IMAP baseline UID set to {Uid}.", _lastSeenUid);
            }

            while (!stoppingToken.IsCancellationRequested) {
                using var done = new CancellationTokenSource(TimeSpan.FromMinutes(idleTimeoutMinutes));
                void onCountChanged(object? sender, EventArgs e) => done.Cancel();
                client.Inbox.CountChanged += onCountChanged;
                try {
                    await client.IdleAsync(done.Token, stoppingToken);
                } catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) {
                    // Count changed or idle timed out — both are normal
                } finally {
                    client.Inbox.CountChanged -= onCountChanged;
                }

                await FetchNewMessagesAsync(client, ourAddress, stoppingToken);
            }

            await client.DisconnectAsync(quit: true, stoppingToken);
        }

        private async Task FetchNewMessagesAsync(ImapClient client, string ourAddress, CancellationToken stoppingToken) {
            // Search for messages addressed to us (be a good neighbour in shared inboxes)
            var toUs = string.IsNullOrWhiteSpace(ourAddress)
                ? SearchQuery.All
                : SearchQuery.Or(SearchQuery.ToContains(ourAddress), SearchQuery.CcContains(ourAddress));

            var matchingUids = await client.Inbox.SearchAsync(toUs, stoppingToken);
            var newUids = matchingUids
                .Where(uid => uid.Id > _lastSeenUid)
                .OrderBy(uid => uid.Id)
                .ToList();

            foreach (var uid in newUids) {
                try {
                    var message = await client.Inbox.GetMessageAsync(uid, stoppingToken);
                    if (messageChannel.TryWrite(message)) {
                        logger.LogDebug("Enqueued INBOX message UID {Uid}.", uid.Id);
                    } else {
                        logger.LogWarning("Email channel full; dropping message UID {Uid}.", uid.Id);
                    }
                } catch (Exception ex) when (!stoppingToken.IsCancellationRequested) {
                    logger.LogWarning(ex, "Failed to fetch message UID {Uid}.", uid.Id);
                }
                _lastSeenUid = uid.Id;
            }
        }

        private static SecureSocketOptions GetImapSocketOptions(ImapOptions imap) {
            if (imap.Tls == "Always") {
                return SecureSocketOptions.SslOnConnect;
            }
            if (imap.Tls == "StartTls") {
                return SecureSocketOptions.StartTls;
            }
            return imap.Port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        }
    }
}
