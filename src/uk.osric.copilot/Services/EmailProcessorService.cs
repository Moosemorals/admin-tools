namespace uk.osric.copilot.Services {
    using System.Diagnostics;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Channels;
    using MimeKit;
    using MimeKit.Cryptography;

    /// <summary>
    /// Background service that drains the inbound email channel and processes each message:
    /// verify S/MIME signature → parse project from subject → find/create session → send to Copilot.
    /// </summary>
    public sealed class EmailProcessorService(
            ChannelReader<MimeMessage> messageChannel,
            CertificateService certificates,
            CopilotService copilot,
            SmtpSenderService smtp,
            EmailMetrics metrics,
            IConfiguration config,
            ILogger<EmailProcessorService> logger) : BackgroundService {

        private static readonly ActivitySource _activitySource = new("uk.osric.copilot");

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            await foreach (var message in messageChannel.ReadAllAsync(stoppingToken)) {
                try {
                    await ProcessMessageAsync(message, stoppingToken);
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    break;
                } catch (Exception ex) {
                    logger.LogError(ex, "Unhandled error processing message {Id}.", message.MessageId);
                }
            }
        }

        private async Task ProcessMessageAsync(MimeMessage message, CancellationToken stoppingToken) {
            metrics.RecordReceived();

            using var activity = _activitySource.StartActivity("email.process");
            activity?.SetTag("messaging.system", "imap");
            activity?.SetTag("messaging.operation.type", "receive");
            activity?.SetTag("messaging.destination.name", "INBOX");
            activity?.SetTag("messaging.message.id", message.MessageId);

            var isSigned = message.Body is MultipartSigned ||
                (message.Body is ApplicationPkcs7Mime p && p.SecureMimeType == SecureMimeType.SignedData);
            if (!isSigned) {
                logger.LogInformation("Dropping unsigned message {Id}.", message.MessageId);
                metrics.RecordDropped("unsigned");
                return;
            }

            DigitalSignatureCollection signatures;
            try {
                using var ctx = new TemporarySecureMimeContext();
                if (message.Body is MultipartSigned signed) {
                    signatures = signed.Verify(ctx);
                } else if (message.Body is ApplicationPkcs7Mime pkcs7) {
                    signatures = pkcs7.Verify(ctx, out _);
                } else {
                    metrics.RecordDropped("unsigned");
                    return;
                }
            } catch (Exception ex) {
                logger.LogWarning(ex, "Signature verification failed for message {Id}.", message.MessageId);
                metrics.RecordDropped("invalid_signature");
                return;
            }

            var firstSig = signatures.FirstOrDefault();
            if (firstSig is null || !firstSig.Verify()) {
                metrics.RecordDropped("invalid_signature");
                return;
            }

            X509Certificate2 senderCert;
            try {
                if (firstSig.SignerCertificate is not SecureMimeDigitalCertificate smimeCert) {
                    throw new InvalidOperationException("Signer certificate is not an S/MIME certificate.");
                }
                senderCert = X509CertificateLoader.LoadCertificate(smimeCert.Certificate.GetEncoded());
            } catch (Exception ex) {
                logger.LogWarning(ex, "Failed to extract signer certificate for message {Id}.", message.MessageId);
                metrics.RecordDropped("invalid_signature");
                return;
            }

            var certRecord = await certificates.ValidateCertificateAsync(senderCert);
            if (certRecord is null) {
                logger.LogInformation("Dropping message {Id}: unknown or invalid certificate.", message.MessageId);
                metrics.RecordDropped("unknown_signature");
                return;
            }

            var rawSubject = message.Subject ?? string.Empty;
            var projectName = StripReplyPrefixes(rawSubject);
            var projectDir = FindProjectDirectory(projectName);

            if (projectDir is null) {
                logger.LogInformation("No project matched '{Subject}'; sending project list.", projectName);
                metrics.RecordReplied("unknown_project");
                await SendProjectListReplyAsync(certRecord.EmailAddress, rawSubject, stoppingToken);
                return;
            }

            var sessions = await copilot.ListSessionsAsync();
            var session = sessions.FirstOrDefault(s =>
                string.Equals(s.WorkingDirectory, projectDir, StringComparison.OrdinalIgnoreCase));
            if (session is null) {
                session = await copilot.CreateSessionAsync(projectDir, certRecord.EmailAddress);
            }

            var body = ExtractTextBody(message);
            await copilot.SendAsync(session.Id, body);
            metrics.RecordProcessed();
            logger.LogInformation("Submitted message {Id} to Copilot session {SessionId}.", message.MessageId, session.Id);
        }

        private async Task SendProjectListReplyAsync(
                string to,
                string originalSubject,
                CancellationToken cancellationToken) {
            var root = config.GetValue<string>("ProjectFoldersPath") ?? string.Empty;
            var projects = string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)
                ? "(none configured)"
                : string.Join("\n", Directory.EnumerateDirectories(root)
                    .Where(d => Directory.Exists(Path.Combine(d, ".git")))
                    .Select(Path.GetFileName));
            await smtp.SendReplyAsync(
                to,
                $"Re: {StripReplyPrefixes(originalSubject)}",
                $"Subject did not match a known project.\n\nKnown projects:\n{projects}",
                cancellationToken);
        }

        private string? FindProjectDirectory(string projectName) {
            if (string.IsNullOrWhiteSpace(projectName)) {
                return null;
            }
            var root = config.GetValue<string>("ProjectFoldersPath") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) {
                return null;
            }
            return Directory.EnumerateDirectories(root)
                .FirstOrDefault(dir =>
                    string.Equals(Path.GetFileName(dir), projectName, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(Path.Combine(dir, ".git")));
        }

        private static readonly string[] _replyPrefixes = ["Re:", "Fwd:", "FW:"];

        internal static string StripReplyPrefixes(string subject) {
            var result = subject.Trim();
            bool changed;
            do {
                changed = false;
                foreach (var prefix in _replyPrefixes) {
                    if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                        result = result[prefix.Length..].Trim();
                        changed = true;
                    }
                }
            } while (changed);
            return result;
        }

        private static string ExtractTextBody(MimeMessage message) =>
            message.TextBody ?? message.HtmlBody ?? string.Empty;
    }
}
