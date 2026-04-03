namespace uk.osric.copilot.Data {
    using Microsoft.EntityFrameworkCore;
    using uk.osric.copilot.Models;

    /// <summary>
    /// EF Core-backed store for session records and message history.
    /// Uses IDbContextFactory to create a new DbContext per operation,
    /// which is safe for concurrent access.
    /// </summary>
    public sealed class SessionRepository(IDbContextFactory<CopilotDbContext> factory) {
        public async Task<IReadOnlyList<Session>> GetAllAsync() {
            await using var db = await factory.CreateDbContextAsync();
            return await db.Sessions
                .OrderByDescending(s => s.LastActiveAt)
                .ToListAsync();
        }

        public async Task UpsertAsync(Session session) {
            await using var db = await factory.CreateDbContextAsync();
            var existing = await db.Sessions.FindAsync(session.Id);
            if (existing is null) {
                db.Sessions.Add(session);
            } else {
                existing.Title = session.Title;
                existing.LastActiveAt = session.LastActiveAt;
                existing.WorkingDirectory = session.WorkingDirectory;
            }
            await db.SaveChangesAsync();
        }

        public async Task TouchAsync(string id, DateTimeOffset lastActiveAt) {
            await using var db = await factory.CreateDbContextAsync();
            await db.Sessions
                .Where(s => s.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastActiveAt, lastActiveAt));
        }

        public async Task DeleteAsync(string id) {
            await using var db = await factory.CreateDbContextAsync();
            await db.Sessions
                .Where(s => s.Id == id)
                .ExecuteDeleteAsync();
        }

        // ── Message persistence ───────────────────────────────────────────────

        /// <summary>
        /// Inserts a message and populates <see cref="SessionMessage.Id"/> on the
        /// passed object with the auto-generated surrogate key.
        /// </summary>
        public async Task AddMessageAsync(SessionMessage message) {
            await using var db = await factory.CreateDbContextAsync();
            db.Messages.Add(message);
            await db.SaveChangesAsync();
            // EF populates message.Id after SaveChanges.
        }

        /// <summary>Returns messages for a session with Id strictly greater than <paramref name="afterId"/>.</summary>
        public async Task<IReadOnlyList<SessionMessage>> GetMessagesAfterAsync(string sessionId, long afterId) {
            await using var db = await factory.CreateDbContextAsync();
            return await db.Messages
                .Where(m => m.SessionId == sessionId && m.Id > afterId)
                .OrderBy(m => m.Id)
                .ToListAsync();
        }

        /// <summary>
        /// Returns all stored messages across all sessions with Id strictly greater
        /// than <paramref name="afterId"/>, ordered by Id. Used to replay missed
        /// events on SSE reconnect.
        /// </summary>
        public async Task<IReadOnlyList<SessionMessage>> GetAllEventsAfterAsync(long afterId) {
            await using var db = await factory.CreateDbContextAsync();
            return await db.Messages
                .Where(m => m.Id > afterId)
                .OrderBy(m => m.Id)
                .ToListAsync();
        }
    }
}
