using Microsoft.EntityFrameworkCore;
using uk.osric.copilot.Models;

namespace uk.osric.copilot.Data;

public class CopilotDbContext(DbContextOptions<CopilotDbContext> options) : DbContext(options) {
    public DbSet<Session> Sessions { get; set; } = null!;

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
    }
}
