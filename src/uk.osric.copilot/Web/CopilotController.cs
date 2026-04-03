namespace uk.osric.copilot.Web {
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.AspNetCore.Mvc.ModelBinding;
    using uk.osric.copilot.Infrastructure;
    using uk.osric.copilot.Models;
    using uk.osric.copilot.Services;

    /// <summary>
    /// Single controller that exposes the full Copilot wrapper HTTP API.
    /// Registered via <see cref="AppServiceExtensions.AddCopilotServices"/> (which calls
    /// <c>AddControllers()</c>) and discovered by <c>app.MapControllers()</c> in Program.
    /// </summary>
    [ApiController]
    public sealed class CopilotController(
            CopilotService copilot,
            SseBroadcaster broadcaster,
            IConfiguration config) : ControllerBase {

        // ── SSE stream ──────────────────────────────────────────────────────────

        /// <summary>
        /// Long-lived SSE endpoint.  Supports the <c>Last-Event-ID</c> reconnect protocol:
        /// on reconnect the browser sends the last id it saw and we replay any persisted
        /// events it missed before draining the live broadcast channel.
        ///
        /// A <c>: heartbeat</c> comment is emitted every 27 seconds to prevent upstream
        /// proxies and load-balancers from closing the idle connection.
        ///
        /// Returns <c>Task</c> (not <c>IActionResult</c>) because the response body is
        /// written directly; MVC leaves the response as-is for void/Task actions.
        /// </summary>
        [HttpGet("/events")]
        public async Task StreamEvents(CancellationToken ct) {
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers.Connection = "keep-alive";
            // Disable nginx response buffering so events reach the browser immediately.
            Response.Headers["X-Accel-Buffering"] = "no";

            await Response.Body.FlushAsync(ct);

            // Subscribe before querying the DB so that no live events are lost in the
            // window between the DB read and the channel drain.
            var reader = broadcaster.Subscribe();
            long lastSentId = 0;

            try {
                // Replay any events the client missed while disconnected.
                var lastEventIdStr = Request.Headers["Last-Event-ID"].FirstOrDefault();
                if (long.TryParse(lastEventIdStr, out var afterId) && afterId > 0) {
                    var missed = await copilot.GetEventsAfterAsync(afterId);
                    foreach (var (id, json) in missed) {
                        await Response.WriteAsync($"id: {id}\ndata: {json}\n\n", ct);
                        lastSentId = id;
                    }
                    await Response.Body.FlushAsync(ct);
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
                        await Response.WriteAsync(": heartbeat\n\n", ct);
                        await Response.Body.FlushAsync(ct);
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
                            await Response.WriteAsync($"id: {msg.Id}\n", ct);
                        }
                        await Response.WriteAsync($"data: {msg.Json}\n\n", ct);
                        await Response.Body.FlushAsync(ct);
                    }
                }
            } catch (OperationCanceledException) {
                // Client disconnected — normal, expected exit path.
            } finally {
                broadcaster.Unsubscribe(reader);
            }
        }

        // ── Project folders ─────────────────────────────────────────────────────

        /// <summary>
        /// Returns the list of git repositories under <c>ProjectFoldersPath</c>.
        /// Returns an empty array when the path is not configured or does not exist.
        /// </summary>
        [HttpGet("/project-folders")]
        public IActionResult GetProjectFolders() {
            var root = config.GetValue<string>("ProjectFoldersPath") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) {
                return Ok(Array.Empty<object>());
            }

            var folders = Directory.EnumerateDirectories(root)
                .Where(dir => Directory.Exists(Path.Combine(dir, ".git")))
                .OrderBy(dir => dir)
                .Select(dir => new { name = Path.GetFileName(dir), path = dir })
                .ToArray();

            return Ok(folders);
        }

        // ── Sessions ────────────────────────────────────────────────────────────

        /// <summary>Returns all sessions ordered by most-recently active.</summary>
        [HttpGet("/sessions")]
        public async Task<IActionResult> ListSessions() {
            return Ok(await copilot.ListSessionsAsync());
        }

        /// <summary>
        /// Creates a new session.  Accepts an optional JSON body
        /// <c>{ "workingDirectory": "..." }</c>; omit the body (or pass no
        /// workingDirectory) to create a session without a project folder.
        /// </summary>
        [HttpPost("/sessions")]
        public async Task<IActionResult> CreateSession(
                [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateSessionRequest? body) {
            string? workingDirectory = null;
            if (!string.IsNullOrWhiteSpace(body?.WorkingDirectory)) {
                var validated = ProjectFolderValidator.Validate(config, body.WorkingDirectory);
                if (validated is null) {
                    return BadRequest("The supplied working directory is not a valid project folder.");
                }
                workingDirectory = validated;
            }

            try {
                return Ok(await copilot.CreateSessionAsync(workingDirectory));
            } catch (InvalidOperationException ex) {
                return Problem(ex.Message, statusCode: 503);
            }
        }

        /// <summary>
        /// Returns stored events for a session with Id &gt; <paramref name="afterId"/>.
        /// Returns an empty array (not 404) when the session has no messages — history
        /// replay is best-effort.
        /// </summary>
        [HttpGet("/sessions/{sessionId}/messages")]
        public async Task<IActionResult> GetMessages(string sessionId, [FromQuery] long afterId = 0) {
            return Ok(await copilot.GetMessagesJsonAsync(sessionId, afterId));
        }

        /// <summary>
        /// Sends a plain-text prompt to the specified session.
        /// Body must be <c>text/plain</c>; returns <c>{ skipped: true }</c> for empty prompts.
        /// </summary>
        [HttpPost("/sessions/{sessionId}/send")]
        public async Task<IActionResult> Send(string sessionId) {
            var prompt = await new StreamReader(Request.Body).ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(prompt)) {
                return Ok(new { skipped = true });
            }

            try {
                return Ok(new { messageId = await copilot.SendAsync(sessionId, prompt.Trim()) });
            } catch (KeyNotFoundException) {
                return NotFound();
            } catch (InvalidOperationException ex) {
                return Problem(ex.Message, statusCode: 503);
            }
        }

        /// <summary>Deletes a session from memory, the SDK, and the database.</summary>
        [HttpDelete("/sessions/{sessionId}")]
        public async Task<IActionResult> DeleteSession(string sessionId) {
            try {
                await copilot.DeleteSessionAsync(sessionId);
                return NoContent();
            } catch (KeyNotFoundException) {
                return NotFound();
            }
        }

        // ── User-input reply ─────────────────────────────────────────────────────

        /// <summary>
        /// Resolves a pending user-input request.  The <c>[ApiController]</c> attribute
        /// returns 400 automatically for missing or malformed JSON bodies, so no
        /// try/catch around deserialisation is needed here.
        /// </summary>
        [HttpPost("/user-input-reply")]
        public IActionResult UserInputReply([FromBody] UserInputReply body) {
            if (string.IsNullOrEmpty(body.RequestId)) {
                return BadRequest("requestId is required.");
            }

            var found = copilot.ReplyUserInput(
                body.RequestId, body.Answer ?? string.Empty, body.WasFreeform);
            return found
                ? Ok()
                : NotFound($"No pending request with id '{body.RequestId}'.");
        }
    }
}
