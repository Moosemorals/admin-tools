using System.Text.Json;
using uk.osric.copilot.Data;
using uk.osric.copilot.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSystemd();

var dbPath     = builder.Configuration.GetValue<string>("DatabasePath") ?? "copilot-sessions.db";
var copilotUrl = builder.Configuration.GetValue<string>("CopilotUrl");
builder.Services.AddSingleton<SessionRepository>(_ => new SessionRepository(dbPath));
builder.Services.AddSingleton<CopilotService>(sp =>
    new CopilotService(sp.GetRequiredService<ILogger<CopilotService>>(),
                       sp.GetRequiredService<SessionRepository>(),
                       copilotUrl));
builder.Services.AddHostedService(sp => sp.GetRequiredService<CopilotService>());

var app = builder.Build();

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

// ── Session list ─────────────────────────────────────────────────────────────
app.MapGet("/sessions", async (CopilotService copilot) =>
{
    var sessions = await copilot.ListSessionsAsync();
    return Results.Ok(sessions);
});

// ── Create session ────────────────────────────────────────────────────────────
app.MapPost("/sessions", async (CopilotService copilot) =>
{
    try
    {
        var record = await copilot.CreateSessionAsync();
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

// ── Request model ─────────────────────────────────────────────────────────────
internal record UserInputReply(string RequestId, string? Answer, bool WasFreeform);

