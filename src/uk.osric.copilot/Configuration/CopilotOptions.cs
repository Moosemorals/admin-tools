namespace uk.osric.copilot.Configuration {
    public sealed class CopilotOptions {
        public EmailOptions Email { get; set; } = new();
    }

    public sealed class EmailOptions {
        public string FromAddress { get; set; } = string.Empty;
        public ImapOptions Imap { get; set; } = new();
        public SmtpOptions Smtp { get; set; } = new();
    }

    public sealed class ImapOptions {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        /// <summary>"Always", "StartTls", or null for auto-detect.</summary>
        public string? Tls { get; set; }
    }

    public sealed class SmtpOptions {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        /// <summary>"Always", "StartTls", or null for auto-detect.</summary>
        public string? Tls { get; set; }
    }
}
