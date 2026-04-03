using CopilotWrapper.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<CopilotService>();
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
            // SSE format: "data: <payload>\n\n"
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

// ── Send a prompt ────────────────────────────────────────────────────────────
app.MapPost("/send", async (CopilotService copilot, HttpContext ctx) =>
{
    var prompt = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(prompt))
        return Results.BadRequest("Prompt must not be empty.");

    try
    {
        var messageId = await copilot.SendAsync(prompt.Trim());
        return Results.Ok(new { messageId });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: 503);
    }
});

app.Run();
