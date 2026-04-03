namespace uk.osric.copilot.Data {
    using Microsoft.EntityFrameworkCore;
    using uk.osric.copilot.Models;

    /// <summary>
    /// EF Core-backed store for session records and message history.
    /// Uses <see cref="IDbContextFactory{TContext}"/> to create a fresh <see cref="CopilotDbContext"/>
    /// per operation, which is safe for concurrent access from multiple async callers.
    /// </summary>
    internal sealed class SessionRepository(IDbContextFactory<CopilotDbContext> factory) {
        /// <summary>Returns all sessions ordered by most-recently active.</summary>
        internal async Task<IReadOnlyList<Session>> GetAllAsync() {
            await using var db = await factory.CreateDbContextAsync();
            return await db.Sessions
                .OrderByDescending(s => s.LastActiveAt)
                .ToListAsync();
        }

        /// <summary>
        /// Inserts <paramref name="session"/> if it does not exist yet; otherwise updates
        /// the title, last-active timestamp, and working directory.
        /// </summary>
        internal async Task UpsertAsync(Session session) {
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

        /// <summary>Updates <c>last_active_at</c> for <paramref name="id"/> without loading the entity.</summary>
        internal async Task TouchAsync(string id, DateTimeOffset lastActiveAt) {
            await using var db = await factory.CreateDbContextAsync();
            await db.Sessions
                .Where(s => s.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastActiveAt, lastActiveAt));
        }

        /// <summary>Deletes the session row (and any messages via cascade if configured).</summary>
        internal async Task DeleteAsync(string id) {
            await using var db = await factory.CreateDbContextAsync();
            await db.Sessions
                .Where(s => s.Id == id)
                .ExecuteDeleteAsync();
        }

        // ── Message persistence ───────────────────────────────────────────────

        /// <summary>
        /// Inserts <paramref name="message"/> and populates <see cref="SessionMessage.Id"/>
        /// with the auto-generated surrogate key after the insert.
        /// </summary>
        internal async Task AddMessageAsync(SessionMessage message) {
            await using var db = await factory.CreateDbContextAsync();
            db.Messages.Add(message);
            await db.SaveChangesAsync();
            // EF populates message.Id after SaveChanges via the AUTOINCREMENT rowid.
        }

        /// <summary>
        /// Returns messages for <paramref name="sessionId"/> with Id strictly greater than
        /// <paramref name="afterId"/>, ordered ascending by Id.
        /// Used by the history endpoint so clients can page forward through the log.
        /// </summary>
        internal async Task<IReadOnlyList<SessionMessage>> GetMessagesAfterAsync(
                string sessionId, long afterId) {
            await using var db = await factory.CreateDbContextAsync();
            return await db.Messages
                .Where(m => m.SessionId == sessionId && m.Id > afterId)
                .OrderBy(m => m.Id)
                .ToListAsync();
        }

        /// <summary>
        /// Returns all messages across all sessions with Id strictly greater than
        /// <paramref name="afterId"/>, ordered ascending by Id.
        /// Used to replay events missed during an SSE reconnect.
        /// </summary>
        internal async Task<IReadOnlyList<SessionMessage>> GetAllEventsAfterAsync(long afterId) {
            await using var db = await factory.CreateDbContextAsync();
            return await db.Messages
                .Where(m => m.Id > afterId)
                .OrderBy(m => m.Id)
                .ToListAsync();
        }
    }
}
