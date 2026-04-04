namespace uk.osric.copilot.Models {
    /// <summary>Persisted Copilot session record (EF Core entity).</summary>
    public class Session {
        public string Id { get; set; } = null!;
        public string Title { get; set; } = null!;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastActiveAt { get; set; }

        /// <summary>
        /// Absolute path to the project folder set when the session was created.
        /// Null for sessions created before this feature was added.
        /// </summary>
        public string? WorkingDirectory { get; set; }
        /// <summary>
        /// Email address of the user who initiated this session via email.
        /// Null for sessions created from the web UI.
        /// </summary>
        public string? EmailAddress { get; set; }
    }
}
