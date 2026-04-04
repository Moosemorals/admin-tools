// SPDX-FileCopyrightText: Copyright (c) 2026 Osric Wilkinson
// SPDX-License-Identifier: MIT

namespace uk.osric.copilot.Services {
    using System.Threading.Channels;
    using MailKit;
    using MailKit.Net.Imap;
    using MailKit.Search;
    using MailKit.Security;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Options;
    using MimeKit;
    using uk.osric.copilot.Configuration;
    using uk.osric.copilot.Data;
    using uk.osric.copilot.Infrastructure;
    using uk.osric.copilot.Models;

    public sealed class ImapListenerService(
            IOptions<CopilotOptions> options,
            ChannelWriter<MimeMessage> messageChannel,
            IDbContextFactory<CopilotDbContext> dbFactory,
            ILogger<ImapListenerService> logger) : BackgroundService {

        // In-memory cache; loaded from DB on first use and updated after each fetch.
        private ImapSyncState? _syncState;

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

            // Enable QRESYNC if the server supports it (RFC 7162).
            // This allows the server to send only changes since our last sync.
            var useQResync = client.Capabilities.HasFlag(ImapCapabilities.QuickResync);
            if (useQResync) {
                await client.EnableQuickResyncAsync(stoppingToken);
            }

            // Load persisted sync state (cached in memory after first load)
            if (_syncState == null) {
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                _syncState = await db.ImapSyncStates.FindAsync(new object[] { 1 }, stoppingToken)
                             ?? new ImapSyncState { Id = 1 };
            }

            // Open inbox — use QRESYNC overload when supported and we have a stored baseline
            if (useQResync && _syncState.UidValidity != 0) {
                await client.Inbox.OpenAsync(
                    FolderAccess.ReadOnly,
                    _syncState.UidValidity,
                    _syncState.HighestModSeq,
                    null,
                    stoppingToken);
            } else {
                await client.Inbox.OpenAsync(FolderAccess.ReadOnly, stoppingToken);
            }

            logger.LogInformation("IMAP connected to {Host}.", imap.Host);

            // Detect mailbox recreation (UidValidity change) and reset state
            var currentUidValidity = client.Inbox.UidValidity;
            if (_syncState.UidValidity != 0 && _syncState.UidValidity != currentUidValidity) {
                logger.LogWarning(
                    "IMAP UidValidity changed ({Old} -> {New}); resetting sync state.",
                    _syncState.UidValidity, currentUidValidity);
                _syncState = new ImapSyncState { Id = 1 };
            }

            _syncState.UidValidity = currentUidValidity;
            _syncState.HighestModSeq = client.Inbox.HighestModSeq;

            // Establish baseline: don't re-process existing messages on first-ever startup
            if (_syncState.LastSeenUid == 0) {
                var existingUids = await client.Inbox.SearchAsync(SearchQuery.All, stoppingToken);
                _syncState.LastSeenUid = existingUids.Count > 0 ? existingUids[existingUids.Count - 1].Id : 0;
                await SaveSyncStateAsync(stoppingToken);
                logger.LogDebug("IMAP baseline UID set to {Uid}.", _syncState.LastSeenUid);
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
                .Where(uid => uid.Id > _syncState!.LastSeenUid)
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
                _syncState!.LastSeenUid = uid.Id;
            }

            if (newUids.Count > 0) {
                _syncState!.HighestModSeq = client.Inbox.HighestModSeq;
                await SaveSyncStateAsync(stoppingToken);
            }
        }

        private async Task SaveSyncStateAsync(CancellationToken ct) {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var existing = await db.ImapSyncStates.FindAsync(new object[] { 1 }, ct);
            if (existing == null) {
                db.ImapSyncStates.Add(new ImapSyncState {
                    Id = 1,
                    UidValidity = _syncState!.UidValidity,
                    HighestModSeq = _syncState.HighestModSeq,
                    LastSeenUid = _syncState.LastSeenUid,
                });
            } else {
                existing.UidValidity = _syncState!.UidValidity;
                existing.HighestModSeq = _syncState.HighestModSeq;
                existing.LastSeenUid = _syncState.LastSeenUid;
            }
            await db.SaveChangesAsync(ct);
        }

        private static SecureSocketOptions GetImapSocketOptions(ImapOptions imap) =>
            TlsHelper.FromTlsString(imap.Tls)
            ?? (imap.Port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
    }
}
