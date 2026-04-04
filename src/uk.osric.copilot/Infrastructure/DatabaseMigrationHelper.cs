namespace uk.osric.copilot.Infrastructure {
    using Microsoft.EntityFrameworkCore;
    using uk.osric.copilot.Data;

    internal static class DatabaseMigrationHelper {
        /// <summary>
        /// Runs any pending EF Core migrations.
        /// Call this once at startup before serving requests.
        /// </summary>
        internal static async Task InitialiseDatabaseAsync(this WebApplication app) {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CopilotDbContext>();
            await db.Database.MigrateAsync();
        }
    }
}
