namespace uk.osric.copilot.Data {
    using Microsoft.EntityFrameworkCore;
    using uk.osric.copilot.Models;

    /// <summary>
    /// EF Core database context.  Kept <c>public</c> so that EF design-time tooling
    /// (<c>dotnet ef migrations add</c>) can locate it via the registered factory.
    /// </summary>
    public class CopilotDbContext(DbContextOptions<CopilotDbContext> options) : DbContext(options) {
        public DbSet<Session> Sessions { get; set; } = null!;
        public DbSet<SessionMessage> Messages { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder) {
            modelBuilder.Entity<Session>(entity => {
                entity.ToTable("sessions");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Title).HasColumnName("title").IsRequired();

                // Store DateTimeOffset as round-trip ISO 8601 text to match the
                // pre-EF schema and keep values timezone-aware.
                entity.Property(e => e.CreatedAt)
                      .HasColumnName("created_at")
                      .HasConversion(
                          v => v.ToString("O"),
                          v => DateTimeOffset.ParseExact(v, "O", null));

                entity.Property(e => e.LastActiveAt)
                      .HasColumnName("last_active_at")
                      .HasConversion(
                          v => v.ToString("O"),
                          v => DateTimeOffset.ParseExact(v, "O", null));

                entity.Property(e => e.WorkingDirectory).HasColumnName("working_directory");
            });

            modelBuilder.Entity<SessionMessage>(entity => {
                entity.ToTable("messages");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
                entity.Property(e => e.EventType).HasColumnName("event_type").IsRequired();
                entity.Property(e => e.Payload).HasColumnName("payload").IsRequired();
                entity.Property(e => e.CreatedAt)
                      .HasColumnName("created_at")
                      .HasConversion(
                          v => v.ToString("O"),
                          v => DateTimeOffset.ParseExact(v, "O", null));
                entity.HasIndex(e => e.SessionId).HasDatabaseName("IX_messages_session_id");
            });
        }
    }
}
