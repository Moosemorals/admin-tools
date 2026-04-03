namespace uk.osric.copilot.Web {
    using System.Text.Json;
    using uk.osric.copilot.Infrastructure;
    using uk.osric.copilot.Models;
    using uk.osric.copilot.Services;

    /// <summary>
    /// Maps all HTTP endpoints for the Copilot wrapper API.
    /// Use <see cref="MapCopilotApi"/> to register them on a <see cref="WebApplication"/>.
    /// </summary>
    internal static class CopilotEndpoints {
        // Shared options for deserialising incoming JSON request bodies.  Case-insensitive
        // so callers can use camelCase, PascalCase, or any mix.
        private static readonly JsonSerializerOptions CaseInsensitive =
            new() { PropertyNameCaseInsensitive = true };

        /// <summary>Registers all Copilot API routes on <paramref name="app"/>.</summary>
        internal static WebApplication MapCopilotApi(this WebApplication app) {
            app.MapGet("/events",                       SseHandler);
            app.MapGet("/project-folders",              ProjectFoldersHandler);
            app.MapGet("/sessions",                     ListSessionsHandler);
            app.MapPost("/sessions",                    CreateSessionHandler);
            app.MapGet("/sessions/{sessionId}/messages", GetMessagesHandler);
            app.MapPost("/sessions/{sessionId}/send",   SendHandler);
            app.MapDelete("/sessions/{sessionId}",      DeleteSessionHandler);
            app.MapPost("/user-input-reply",            UserInputReplyHandler);
            return app;
        }

        // ── SSE stream ──────────────────────────────────────────────────────────

        /// <summary>
        /// Long-lived SSE endpoint.  Supports the <c>Last-Event-ID</c> reconnect protocol:
        /// on reconnect the browser automatically sends the last id it saw and we replay
        /// any persisted events it missed before draining the live broadcast channel.
        ///
        /// A <c>: heartbeat</c> comment is emitted every 27 seconds to prevent upstream
        /// proxies and load-balancers from closing the idle connection.
        /// </summary>
        private static async Task SseHandler(
                SseBroadcaster broadcaster,
                CopilotService copilot,
                HttpContext ctx,
                CancellationToken ct) {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";
            // Disable nginx response buffering so events reach the browser immediately.
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            await ctx.Response.Body.FlushAsync(ct);

            // Subscribe before querying the DB so that no live events are lost in the
            // window between the DB read and the channel drain.
            var reader = broadcaster.Subscribe();
            long lastSentId = 0;

            try {
                // Replay any events the client missed while disconnected.
                var lastEventIdStr = ctx.Request.Headers["Last-Event-ID"].FirstOrDefault();
                if (long.TryParse(lastEventIdStr, out var afterId) && afterId > 0) {
                    var missed = await copilot.GetEventsAfterAsync(afterId);
                    foreach (var (id, json) in missed) {
                        await ctx.Response.WriteAsync($"id: {id}\ndata: {json}\n\n", ct);
                        lastSentId = id;
                    }
                    await ctx.Response.Body.FlushAsync(ct);
                }

                // Drain the live channel.  WaitToReadAsync is given a 27-second timeout;
                // if no message arrives in that window we emit a keepalive comment instead.
                while (true) {
                    bool hasData;
                    try {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(27));
                        hasData = await reader.WaitToReadAsync(timeoutCts.Token);
                    } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                        // 27-second timeout with no messages — send a keepalive comment.
                        // SSE comment lines (starting with ':') are ignored by clients
                        // but prevent proxies from closing the idle connection.
                        await ctx.Response.WriteAsync(": heartbeat\n\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                        continue;
                    }

                    if (!hasData) {
                        break; // broadcaster completed — service is shutting down
                    }

                    while (reader.TryRead(out var msg)) {
                        // Skip messages already delivered in the replay phase above.
                        if (msg.Id.HasValue && msg.Id.Value <= lastSentId) {
                            continue;
                        }
                        if (msg.Id.HasValue) {
                            await ctx.Response.WriteAsync($"id: {msg.Id}\n", ct);
                        }
                        await ctx.Response.WriteAsync($"data: {msg.Json}\n\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                }
            } catch (OperationCanceledException) {
                // Client disconnected — normal, expected exit path.
            } finally {
                broadcaster.Unsubscribe(reader);
            }
        }

        // ── Project folders ─────────────────────────────────────────────────────

        private static IResult ProjectFoldersHandler(IConfiguration config) {
            var root = config.GetValue<string>("ProjectFoldersPath") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) {
                return Results.Ok(Array.Empty<object>());
            }

            var folders = Directory.EnumerateDirectories(root)
                .Where(dir => Directory.Exists(Path.Combine(dir, ".git")))
                .OrderBy(dir => dir)
                .Select(dir => new { name = Path.GetFileName(dir), path = dir })
                .ToArray();

            return Results.Ok(folders);
        }

        // ── Sessions ────────────────────────────────────────────────────────────

        private static async Task<IResult> ListSessionsHandler(CopilotService copilot) {
            var sessions = await copilot.ListSessionsAsync();
            return Results.Ok(sessions);
        }

        private static async Task<IResult> CreateSessionHandler(
                CopilotService copilot,
                HttpContext ctx) {
            // Accept an optional JSON body: { "workingDirectory": "..." }
            CreateSessionRequest? body = null;
            if (ctx.Request.ContentLength > 0 ||
                ctx.Request.Headers.ContentType.ToString().Contains("json")) {
                try {
                    body = await JsonSerializer.DeserializeAsync<CreateSessionRequest>(
                        ctx.Request.Body, CaseInsensitive);
                } catch { /* Invalid JSON — treat as no body. */ }
            }

            string? workingDirectory = null;
            if (!string.IsNullOrWhiteSpace(body?.WorkingDirectory)) {
                var validated = ProjectFolderValidator.Validate(
                    ctx.RequestServices.GetRequiredService<IConfiguration>(),
                    body.WorkingDirectory);

                if (validated is null) {
                    return Results.BadRequest(
                        "The supplied working directory is not a valid project folder.");
                }

                workingDirectory = validated;
            }

            try {
                var record = await copilot.CreateSessionAsync(workingDirectory);
                return Results.Ok(record);
            } catch (InvalidOperationException ex) {
                return Results.Problem(ex.Message, statusCode: 503);
            }
        }

        private static async Task<IResult> GetMessagesHandler(
                CopilotService copilot,
                string sessionId,
                long afterId = 0) {
            // Returns 200 with an empty array when the session exists but has no messages,
            // and also when sessionId is unknown — history replay is best-effort.
            var messages = await copilot.GetMessagesJsonAsync(sessionId, afterId);
            return Results.Ok(messages);
        }

        private static async Task<IResult> SendHandler(
                CopilotService copilot,
                string sessionId,
                HttpContext ctx) {
            var prompt = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(prompt)) {
                return Results.Ok(new { skipped = true });
            }

            try {
                var messageId = await copilot.SendAsync(sessionId, prompt.Trim());
                return Results.Ok(new { messageId });
            } catch (KeyNotFoundException) {
                return Results.NotFound();
            } catch (InvalidOperationException ex) {
                return Results.Problem(ex.Message, statusCode: 503);
            }
        }

        private static async Task<IResult> DeleteSessionHandler(
                CopilotService copilot,
                string sessionId) {
            try {
                await copilot.DeleteSessionAsync(sessionId);
                return Results.NoContent();
            } catch (KeyNotFoundException) {
                return Results.NotFound();
            }
        }

        // ── User-input reply ─────────────────────────────────────────────────────

        private static async Task<IResult> UserInputReplyHandler(
                CopilotService copilot,
                HttpContext ctx) {
            UserInputReply? body;
            try {
                body = await JsonSerializer.DeserializeAsync<UserInputReply>(
                    ctx.Request.Body, CaseInsensitive);
            } catch {
                return Results.BadRequest("Invalid JSON body.");
            }

            if (body is null || string.IsNullOrEmpty(body.RequestId)) {
                return Results.BadRequest("requestId is required.");
            }

            var found = copilot.ReplyUserInput(
                body.RequestId, body.Answer ?? string.Empty, body.WasFreeform);
            return found
                ? Results.Ok()
                : Results.NotFound($"No pending request with id '{body.RequestId}'.");
        }
    }
}
