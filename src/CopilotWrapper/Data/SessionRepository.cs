using Microsoft.Data.Sqlite;
using CopilotWrapper.Models;

namespace CopilotWrapper.Data;

/// <summary>
/// SQLite-backed store for session records owned by this UI instance.
/// </summary>
public sealed class SessionRepository : IDisposable, IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public SessionRepository(string dbPath)
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                id              TEXT PRIMARY KEY,
                title           TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                last_active_at  TEXT NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task<IReadOnlyList<SessionRecord>> GetAllAsync()
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT id, title, created_at, last_active_at FROM sessions ORDER BY last_active_at DESC";

        var list = new List<SessionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new SessionRecord(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                DateTimeOffset.Parse(reader.GetString(3))));
        }

        return list;
    }

    public async Task UpsertAsync(SessionRecord record)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (id, title, created_at, last_active_at)
            VALUES ($id, $title, $created_at, $last_active_at)
            ON CONFLICT(id) DO UPDATE SET
                title          = excluded.title,
                last_active_at = excluded.last_active_at
            """;
        cmd.Parameters.AddWithValue("$id", record.Id);
        cmd.Parameters.AddWithValue("$title", record.Title);
        cmd.Parameters.AddWithValue("$created_at", record.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$last_active_at", record.LastActiveAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task TouchAsync(string id, DateTimeOffset lastActiveAt)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "UPDATE sessions SET last_active_at = $last_active_at WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$last_active_at", lastActiveAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string id)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose() => _connection.Dispose();

    public async ValueTask DisposeAsync()
    {
        await _connection.CloseAsync();
        await _connection.DisposeAsync();
    }
}
