using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using uk.osric.copilot.Data;
using uk.osric.copilot.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSystemd();

// ── EF Core ───────────────────────────────────────────────────────────────────
var dbPath = builder.Configuration.GetValue<string>("DatabasePath") ?? "copilot-sessions.db";
builder.Services.AddDbContextFactory<CopilotDbContext>(opts =>
    opts.UseSqlite($"Data Source={dbPath}"));

// ── Application services ──────────────────────────────────────────────────────
var copilotUrl = builder.Configuration.GetValue<string>("CopilotUrl");
builder.Services.AddSingleton<SessionRepository>();
builder.Services.AddSingleton<CopilotService>(sp =>
    new CopilotService(sp.GetRequiredService<ILogger<CopilotService>>(),
                       sp.GetRequiredService<SessionRepository>(),
                       copilotUrl));
builder.Services.AddHostedService(sp => sp.GetRequiredService<CopilotService>());

var app = builder.Build();

// ── Database migration ────────────────────────────────────────────────────────
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<CopilotDbContext>();

    // Backward compatibility: if a pre-EF raw-SQL database already has a
    // 'sessions' table but no '__EFMigrationsHistory' table, stamp the
    // InitialCreate migration as done so MigrateAsync only applies the
    // AddWorkingDirectory migration (and any future ones).
    await StampPreEfDatabaseAsync(db);
    await db.Database.MigrateAsync();
}

app.UseDefaultFiles();
app.UseStaticFiles();

// ── SSE stream ──────────────────────────────────────────────────────────────
app.MapGet("/events", async (CopilotService copilot, HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    await ctx.Response.Body.FlushAsync(ct);

    var reader = copilot.Subscribe();
    try
    {
        await foreach (var json in reader.ReadAllAsync(ct))
        {
            await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException)
    {
        // Client disconnected – normal exit.
    }
    finally
    {
        copilot.Unsubscribe(reader);
    }
});

// ── Project folders ────────────────────────────────────────────────────────────
app.MapGet("/project-folders", (IConfiguration config) =>
{
    var root = config.GetValue<string>("ProjectFoldersPath") ?? string.Empty;
    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        return Results.Ok(Array.Empty<object>());

    var folders = Directory.EnumerateDirectories(root)
        .Where(dir => Directory.Exists(Path.Combine(dir, ".git")))
        .OrderBy(dir => dir)
        .Select(dir => new { name = Path.GetFileName(dir), path = dir })
        .ToArray();

    return Results.Ok(folders);
});

// ── Session list ─────────────────────────────────────────────────────────────
app.MapGet("/sessions", async (CopilotService copilot) =>
{
    var sessions = await copilot.ListSessionsAsync();
    return Results.Ok(sessions);
});

// ── Create session ────────────────────────────────────────────────────────────
app.MapPost("/sessions", async (CopilotService copilot, HttpContext ctx) =>
{
    // Accept an optional JSON body: { "workingDirectory": "..." }
    CreateSessionRequest? body = null;
    if (ctx.Request.ContentLength > 0 ||
        ctx.Request.Headers.ContentType.ToString().Contains("json"))
    {
        try
        {
            body = await JsonSerializer.DeserializeAsync<CreateSessionRequest>(
                ctx.Request.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { /* invalid JSON – treat as no body */ }
    }

    string? workingDirectory = null;
    if (!string.IsNullOrWhiteSpace(body?.WorkingDirectory))
    {
        var validated = ValidateProjectFolder(
            ctx.RequestServices.GetRequiredService<IConfiguration>(),
            body.WorkingDirectory);

        if (validated is null)
            return Results.BadRequest("The supplied working directory is not a valid project folder.");

        workingDirectory = validated;
    }

    try
    {
        var record = await copilot.CreateSessionAsync(workingDirectory);
        return Results.Ok(record);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: 503);
    }
});

// ── Session message history ───────────────────────────────────────────────────
app.MapGet("/sessions/{sessionId}/messages", async (CopilotService copilot, string sessionId) =>
{
    try
    {
        var messages = await copilot.GetMessagesJsonAsync(sessionId);
        return Results.Ok(messages);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

// ── Send a prompt to a session ────────────────────────────────────────────────
app.MapPost("/sessions/{sessionId}/send",
    async (CopilotService copilot, string sessionId, HttpContext ctx) =>
    {
        var prompt = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(prompt))
            return Results.Ok(new { skipped = true });

        try
        {
            var messageId = await copilot.SendAsync(sessionId, prompt.Trim());
            return Results.Ok(new { messageId });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: 503);
        }
    });

// ── Delete a session ──────────────────────────────────────────────────────────
app.MapDelete("/sessions/{sessionId}", async (CopilotService copilot, string sessionId) =>
{
    try
    {
        await copilot.DeleteSessionAsync(sessionId);
        return Results.NoContent();
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

// ── Reply to a user-input request ─────────────────────────────────────────────
app.MapPost("/user-input-reply", async (CopilotService copilot, HttpContext ctx) =>
{
    UserInputReply? body;
    try
    {
        body = await JsonSerializer.DeserializeAsync<UserInputReply>(
            ctx.Request.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch
    {
        return Results.BadRequest("Invalid JSON body.");
    }

    if (body is null || string.IsNullOrEmpty(body.RequestId))
        return Results.BadRequest("requestId is required.");

    var found = copilot.ReplyUserInput(body.RequestId, body.Answer ?? string.Empty, body.WasFreeform);
    return found
        ? Results.Ok()
        : Results.NotFound($"No pending request with id '{body.RequestId}'.");
});

app.Run();

// ── Helpers ───────────────────────────────────────────────────────────────────

/// <summary>
/// Validates that <paramref name="path"/> is an allowed project folder:
/// it must be a direct child of the configured ProjectFoldersPath and
/// must contain a .git directory.
/// Returns the canonical absolute path, or null if validation fails.
/// </summary>
static string? ValidateProjectFolder(IConfiguration config, string path)
{
    var root = config.GetValue<string>("ProjectFoldersPath") ?? string.Empty;
    if (string.IsNullOrWhiteSpace(root)) return null;

    // Resolve to canonical paths to prevent traversal.
    string canonical;
    try { canonical = Path.GetFullPath(path); }
    catch { return null; }

    var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;

    // Must be a direct child (no deeper nesting).
    if (!canonical.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        return null;

    var relative = canonical[rootFull.Length..];
    if (relative.Contains(Path.DirectorySeparatorChar)) return null; // nested

    // Must exist and have a .git folder.
    if (!Directory.Exists(canonical)) return null;
    if (!Directory.Exists(Path.Combine(canonical, ".git"))) return null;

    return canonical;
}

/// <summary>
/// Stamps the pre-EF 'InitialCreate' migration as already applied when an
/// existing raw-SQL database is detected (sessions table exists but no
/// EF migrations history table).  This lets <c>MigrateAsync</c> run only
/// future migrations on upgrade.  Also adds the working_directory column if
/// it is missing from the legacy schema.
/// </summary>
static async Task StampPreEfDatabaseAsync(CopilotDbContext db)
{
    // Check whether the EF migrations history table already exists.
    bool historyExists;
    try
    {
        await db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM __EFMigrationsHistory LIMIT 1");
        historyExists = true;
    }
    catch { historyExists = false; }

    if (historyExists) return;

    // No history table.  Check for the pre-existing sessions table.
    bool sessionsExist;
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM sessions LIMIT 1");
        sessionsExist = true;
    }
    catch { sessionsExist = false; }

    if (!sessionsExist) return; // Fresh install – let MigrateAsync do everything.

    // Existing raw-SQL database: ensure the working_directory column exists.
    try
    {
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE sessions ADD COLUMN working_directory TEXT");
    }
    catch { /* Column already exists – that's fine. */ }

    // Create the history table and stamp InitialCreate as already applied
    // so MigrateAsync won't try to recreate the sessions table.
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

// ── Request models ─────────────────────────────────────────────────────────────
internal record UserInputReply(string RequestId, string? Answer, bool WasFreeform);
internal record CreateSessionRequest(string? WorkingDirectory);


