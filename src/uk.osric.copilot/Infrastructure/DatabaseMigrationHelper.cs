namespace uk.osric.copilot.Infrastructure {
    using Microsoft.EntityFrameworkCore;
    using uk.osric.copilot.Data;

    internal static class DatabaseMigrationHelper {
        /// <summary>
        /// Runs any pending EF Core migrations.  Also stamps legacy (pre-EF) databases so
        /// that <c>MigrateAsync</c> does not try to recreate tables that already exist.
        /// Call this once at startup before serving requests.
        /// </summary>
        internal static async Task InitialiseDatabaseAsync(this WebApplication app) {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CopilotDbContext>();
            await StampPreEfDatabaseAsync(db);
            await db.Database.MigrateAsync();
        }

        /// <summary>
        /// Detects an existing raw-SQL database (sessions table present, no EF history table)
        /// and stamps the <c>InitialCreate</c> migration as applied so that
        /// <c>MigrateAsync</c> only runs migrations added after the EF adoption.
        /// </summary>
        private static async Task StampPreEfDatabaseAsync(CopilotDbContext db) {
            // If the EF history table already exists there is nothing to stamp.
            bool historyExists;
            try {
                await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM __EFMigrationsHistory LIMIT 1");
                historyExists = true;
            } catch {
                historyExists = false;
            }

            if (historyExists) {
                return;
            }

            // No history table — check whether the pre-EF sessions table exists.
            bool sessionsExist;
            try {
                await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM sessions LIMIT 1");
                sessionsExist = true;
            } catch {
                sessionsExist = false;
            }

            if (!sessionsExist) {
                // Fresh install — let MigrateAsync create everything from scratch.
                return;
            }

            // Existing raw-SQL database: add the working_directory column if it was added
            // after the original schema was created.
            try {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE sessions ADD COLUMN working_directory TEXT");
            } catch { /* Column already exists — harmless. */ }

            // Create the EF history table and mark InitialCreate as already applied
            // so MigrateAsync skips it and only runs newer migrations.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (
                    MigrationId    TEXT NOT NULL,
                    ProductVersion TEXT NOT NULL,
                    PRIMARY KEY (MigrationId)
                )
                """);

            await db.Database.ExecuteSqlRawAsync("""
                INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion)
                VALUES ('20260403105750_InitialCreate', '10.0.5')
                """);
        }
    }
}
