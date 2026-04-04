namespace uk.osric.copilot.Infrastructure {
    using MailKit.Security;

    /// <summary>
    /// Helpers for resolving MailKit <see cref="SecureSocketOptions"/> from configuration strings.
    /// </summary>
    internal static class TlsHelper {
        /// <summary>
        /// Maps a user-supplied TLS mode string to a <see cref="SecureSocketOptions"/> value.
        /// Returns <c>null</c> when the string is unrecognised, signalling that the caller
        /// should apply its own port-based default.
        /// </summary>
        internal static SecureSocketOptions? FromTlsString(string? tls) => tls switch {
            "Always" => SecureSocketOptions.SslOnConnect,
            "StartTls" => SecureSocketOptions.StartTls,
            _ => null,
        };
    }
}
