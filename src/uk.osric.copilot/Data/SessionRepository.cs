using Microsoft.EntityFrameworkCore;
using uk.osric.copilot.Models;

namespace uk.osric.copilot.Data;

/// <summary>
/// EF Core-backed store for session records.
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
}
