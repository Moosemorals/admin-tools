namespace uk.osric.copilot.Configuration {
    public sealed class CopilotOptions {
        public string DatabasePath { get; set; } = "copilot-sessions.db";
        public string? CopilotUrl { get; set; }
        public string ProjectFoldersPath { get; set; } = string.Empty;
        public EmailOptions Email { get; set; } = new();
    }

    public sealed class EmailOptions {
        public string FromAddress { get; set; } = string.Empty;
        public int ChannelCapacity { get; set; } = 16;
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
        public int IdleTimeoutMinutes { get; set; } = 27;
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
