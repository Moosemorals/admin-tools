namespace uk.osric.copilot.Services {
    using System.Diagnostics;
    using MailKit.Net.Smtp;
    using MailKit.Security;
    using Microsoft.Extensions.Options;
    using MimeKit;
    using MimeKit.Cryptography;
    using uk.osric.copilot.Configuration;

    public sealed class SmtpSenderService(
            IOptions<CopilotOptions> options,
            CertificateService certificates,
            ILogger<SmtpSenderService> logger) {

        private static readonly ActivitySource _activitySource = new("uk.osric.copilot");

        public async Task SendReplyAsync(
                string to,
                string subject,
                string body,
                string? inReplyTo = null,
                string? references = null,
                CancellationToken cancellationToken = default) {
            using var activity = _activitySource.StartActivity("smtp.send");
            activity?.SetTag("messaging.system", "smtp");
            activity?.SetTag("messaging.operation.type", "send");

            var smtpOpts = options.Value.Email.Smtp;
            var fromAddress = options.Value.Email.FromAddress;

            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(fromAddress));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;

            if (!string.IsNullOrEmpty(inReplyTo)) {
                message.InReplyTo = inReplyTo;
                if (!string.IsNullOrEmpty(references)) {
                    foreach (var r in references.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                        message.References.Add(r);
                    }
                }
                if (!message.References.Contains(inReplyTo)) {
                    message.References.Add(inReplyTo);
                }
            }

            MimeEntity bodyEntity = new TextPart("plain") { Text = body };

            var activeCerts = await certificates.GetActiveCertificatesAsync(fromAddress);
            var certRecord = activeCerts.FirstOrDefault();
            if (certRecord is not null) {
                try {
                    using var ctx = new TemporarySecureMimeContext();
                    using var pfxStream = new MemoryStream(certRecord.PfxData);
                    var signer = new CmsSigner(pfxStream, string.Empty) {
                        DigestAlgorithm = DigestAlgorithm.Sha256,
                    };
                    bodyEntity = await MultipartSigned.CreateAsync(ctx, signer, bodyEntity, cancellationToken);
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Failed to sign outbound email; sending unsigned.");
                }
            }

            message.Body = bodyEntity;

            try {
                using var client = new SmtpClient();
                await client.ConnectAsync(smtpOpts.Host, smtpOpts.Port, GetSmtpSocketOptions(smtpOpts), cancellationToken);
                if (!string.IsNullOrEmpty(smtpOpts.Username)) {
                    await client.AuthenticateAsync(smtpOpts.Username, smtpOpts.Password, cancellationToken);
                }
                await client.SendAsync(message, cancellationToken);
                await client.DisconnectAsync(quit: true, cancellationToken);
                logger.LogInformation("Sent email to {To} with subject {Subject}.", to, subject);
            } catch (Exception ex) {
                logger.LogError(ex, "Failed to send email to {To}.", to);
                throw;
            }
        }

        private static SecureSocketOptions GetSmtpSocketOptions(SmtpOptions smtp) {
            if (smtp.Tls == "Always") return SecureSocketOptions.SslOnConnect;
            if (smtp.Tls == "StartTls") return SecureSocketOptions.StartTls;
            return smtp.Port == 465 ? SecureSocketOptions.SslOnConnect
                : smtp.Port == 587 ? SecureSocketOptions.StartTls
                : SecureSocketOptions.Auto;
        }
    }
}
