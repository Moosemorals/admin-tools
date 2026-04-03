namespace uk.osric.copilot.Tests.Helpers {
    using Microsoft.Data.Sqlite;
    using Microsoft.EntityFrameworkCore;
    using uk.osric.copilot.Data;

    /// <summary>
    /// IDbContextFactory backed by a shared, always-open SQLite in-memory connection so that
    /// all DbContext instances see the same schema and data within a single test.
    /// </summary>
    internal sealed class TestDbContextFactory : IDbContextFactory<CopilotDbContext>, IAsyncDisposable {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<CopilotDbContext> _options;

        internal TestDbContextFactory() {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _options = new DbContextOptionsBuilder<CopilotDbContext>()
                .UseSqlite(_connection)
                .Options;
        }

        public CopilotDbContext CreateDbContext() => new(_options);

        public Task<CopilotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());

        /// <summary>Creates the schema. Call once before using the factory in a test.</summary>
        internal async Task InitialiseAsync() {
            await using var ctx = CreateDbContext();
            await ctx.Database.EnsureCreatedAsync();
        }

        public async ValueTask DisposeAsync() {
            await _connection.DisposeAsync();
        }
    }
}
