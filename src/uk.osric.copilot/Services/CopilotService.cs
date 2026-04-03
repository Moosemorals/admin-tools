using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using uk.osric.copilot.Data;
using uk.osric.copilot.Models;
using GitHub.Copilot.SDK;

namespace uk.osric.copilot.Services;

/// <summary>
/// Hosted service that manages a GitHub Copilot CLI client and multiple sessions,
/// broadcasting session events to all connected SSE subscribers.
/// </summary>
public sealed class CopilotService : IHostedService, IAsyncDisposable
{
    // ── Per-session state ─────────────────────────────────────────────────────

    private sealed class SessionState(CopilotSession session, IDisposable subscription)
    {
        public CopilotSession Session { get; } = session;
        public IDisposable Subscription { get; } = subscription;

        /// <summary>Pending user-input requests keyed by our generated requestId.</summary>
        public Dictionary<string, TaskCompletionSource<UserInputResponse>> PendingInputs { get; } = new();
        public Lock PendingInputsLock { get; } = new();
    }

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly ILogger<CopilotService> _logger;
    private readonly SessionRepository _db;
    private readonly string? _copilotUrl;
    private readonly JsonSerializerOptions _jsonOptions;

    private CopilotClient? _client;

    private readonly Dictionary<string, SessionState> _sessions = new();
    private readonly Lock _sessionsLock = new();

    private readonly List<Channel<string>> _subscribers = [];
    private readonly Lock _subscriberLock = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    public CopilotService(ILogger<CopilotService> logger, SessionRepository db, string? copilotUrl = null)
    {
        _logger = logger;
        _db = db;
        _copilotUrl = copilotUrl;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Copilot client…");
        var options = string.IsNullOrWhiteSpace(_copilotUrl)
            ? new CopilotClientOptions()
            : new CopilotClientOptions { CliUrl = _copilotUrl };
        _client = new CopilotClient(options);
        await _client.StartAsync(cancellationToken);

        // Resume all sessions previously created in this UI.
        var stored = await _db.GetAllAsync();
        foreach (var record in stored)
        {
            try
            {
                await ResumeSessionCoreAsync(record.Id, record.WorkingDirectory, cancellationToken);
                _logger.LogInformation("Resumed session {Id}.", record.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resume session {Id}; removing from storage.", record.Id);
                await _db.DeleteAsync(record.Id);
            }
        }

        _logger.LogInformation("Copilot service ready.");
        BroadcastRaw(BuildEventJson("ServiceReady", new { }));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Copilot service…");

        List<SessionState> snapshot;
        lock (_sessionsLock)
        {
            snapshot = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var state in snapshot)
        {
            CancelPendingInputs(state);
            state.Subscription.Dispose();
            await state.Session.DisposeAsync();
        }

        if (_client is not null)
            await _client.StopAsync();
    }

    // ── Session management ────────────────────────────────────────────────────

    /// <summary>Creates a new Copilot session, persists it, and broadcasts <c>SessionCreated</c>.</summary>
    public async Task<Session> CreateSessionAsync(string? workingDirectory = null)
    {
        if (_client is null)
            throw new InvalidOperationException("Copilot client is not running.");

        var sessionId = Guid.NewGuid().ToString("N");
        var session = await _client.CreateSessionAsync(new SessionConfig
        {
            SessionId = sessionId,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            OnUserInputRequest = BuildUserInputHandler(sessionId),
            WorkingDirectory = workingDirectory,
        });

        var now = DateTimeOffset.UtcNow;
        var title = $"Session {now:HH:mm:ss}";
        var record = new Session
        {
            Id = sessionId,
            Title = title,
            CreatedAt = now,
            LastActiveAt = now,
            WorkingDirectory = workingDirectory,
        };

        await _db.UpsertAsync(record);
        RegisterSession(sessionId, session);

        _logger.LogInformation("Created session {Id}.", sessionId);
        BroadcastRaw(BuildEventJson("SessionCreated", new
        {
            sessionId,
            title,
            createdAt = record.CreatedAt,
            lastActiveAt = record.LastActiveAt,
            workingDirectory,
        }));

        return record;
    }

    public Task<IReadOnlyList<Session>> ListSessionsAsync() => _db.GetAllAsync();

    /// <summary>
    /// Returns the complete event history for a session, serialised in the same
    /// format as live SSE events so the frontend can render them identically.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> GetMessagesJsonAsync(string sessionId)
    {
        var state = GetState(sessionId);
        var events = await state.Session.GetMessagesAsync();

        var result = new List<JsonElement>(events.Count);
        foreach (var evt in events)
        {
            var json = BuildEventJson(evt.GetType().Name, evt, evt.GetType(), sessionId: sessionId);
            result.Add(JsonDocument.Parse(json).RootElement.Clone());
        }

        return result;
    }

    /// <summary>Sends a prompt to the specified session.</summary>
    public async Task<string> SendAsync(string sessionId, string prompt)
    {
        var state = GetState(sessionId);
        var msgId = await state.Session.SendAsync(new MessageOptions { Prompt = prompt });
        await _db.TouchAsync(sessionId, DateTimeOffset.UtcNow);
        return msgId;
    }

    /// <summary>Deletes a session from memory, the SDK, and the database.</summary>
    public async Task DeleteSessionAsync(string sessionId)
    {
        SessionState? state;
        lock (_sessionsLock)
            _sessions.Remove(sessionId, out state);

        if (state is not null)
        {
            CancelPendingInputs(state);
            state.Subscription.Dispose();
            await state.Session.DisposeAsync();
        }

        if (_client is not null)
            await _client.DeleteSessionAsync(sessionId);

        await _db.DeleteAsync(sessionId);
        _logger.LogInformation("Deleted session.");
    }

    /// <summary>
    /// Completes a pending user-input request.
    /// Returns <c>true</c> if found, <c>false</c> if the requestId is unknown.
    /// </summary>
    public bool ReplyUserInput(string requestId, string answer, bool wasFreeform)
    {
        List<SessionState> snapshot;
        lock (_sessionsLock)
            snapshot = [.. _sessions.Values];

        foreach (var state in snapshot)
        {
            TaskCompletionSource<UserInputResponse>? tcs;
            lock (state.PendingInputsLock)
            {
                if (!state.PendingInputs.Remove(requestId, out tcs))
                    continue;
            }

            tcs.TrySetResult(new UserInputResponse { Answer = answer, WasFreeform = wasFreeform });
            return true;
        }

        return false;
    }

    // ── SSE subscribers ───────────────────────────────────────────────────────

    /// <summary>Subscribes to the global event stream. Caller must call <see cref="Unsubscribe"/> when done.</summary>
    public ChannelReader<string> Subscribe()
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        lock (_subscriberLock)
            _subscribers.Add(channel);

        return channel.Reader;
    }

    /// <summary>Removes a subscriber channel (call when the SSE connection closes).</summary>
    public void Unsubscribe(ChannelReader<string> reader)
    {
        lock (_subscriberLock)
            _subscribers.RemoveAll(c => c.Reader == reader);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task ResumeSessionCoreAsync(string sessionId, string? workingDirectory, CancellationToken cancellationToken)
    {
        var session = await _client!.ResumeSessionAsync(sessionId, new ResumeSessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            OnUserInputRequest = BuildUserInputHandler(sessionId),
            WorkingDirectory = workingDirectory,
        }, cancellationToken);

        RegisterSession(sessionId, session);
    }

    private void RegisterSession(string sessionId, CopilotSession session)
    {
        var sub = session.On(evt => OnSessionEvent(sessionId, evt));
        lock (_sessionsLock)
            _sessions[sessionId] = new SessionState(session, sub);
    }

    private UserInputHandler BuildUserInputHandler(string sessionId) =>
        async (request, _) =>
        {
            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<UserInputResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            SessionState? state;
            lock (_sessionsLock)
                _sessions.TryGetValue(sessionId, out state);

            if (state is null)
                return new UserInputResponse { Answer = string.Empty, WasFreeform = true };

            lock (state.PendingInputsLock)
                state.PendingInputs[requestId] = tcs;

            BroadcastRaw(BuildEventJson("UserInputRequested", new
            {
                requestId,
                question = request.Question,
                choices = request.Choices,
                allowFreeform = request.AllowFreeform,
            }, sessionId: sessionId));

            try
            {
                return await tcs.Task;
            }
            catch (OperationCanceledException)
            {
                lock (state.PendingInputsLock)
                    state.PendingInputs.Remove(requestId);
                return new UserInputResponse { Answer = string.Empty, WasFreeform = true };
            }
        };

    private void OnSessionEvent(string sessionId, SessionEvent evt)
    {
        try
        {
            BroadcastRaw(BuildEventJson(evt.GetType().Name, evt, evt.GetType(), sessionId: sessionId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to serialize session event {Type}.", evt.GetType().Name);
        }
    }

    private SessionState GetState(string sessionId)
    {
        lock (_sessionsLock)
        {
            if (_sessions.TryGetValue(sessionId, out var state))
                return state;
        }

        throw new KeyNotFoundException($"Session '{sessionId}' not found.");
    }

    private static void CancelPendingInputs(SessionState state)
    {
        lock (state.PendingInputsLock)
        {
            foreach (var tcs in state.PendingInputs.Values)
                tcs.TrySetCanceled();
            state.PendingInputs.Clear();
        }
    }

    private string BuildEventJson(
        string eventType,
        object payload,
        Type? runtimeType = null,
        string? sessionId = null)
    {
        var node = JsonSerializer.SerializeToNode(payload, runtimeType ?? payload.GetType(), _jsonOptions)
                   ?? new JsonObject();
        node["_eventType"] = eventType;
        node["_timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        if (sessionId is not null)
            node["_sessionId"] = sessionId;
        return node.ToJsonString(_jsonOptions);
    }

    private void BroadcastRaw(string json)
    {
        Channel<string>[] snapshot;
        lock (_subscriberLock)
            snapshot = [.. _subscribers];

        foreach (var channel in snapshot)
        {
            if (!channel.Writer.TryWrite(json))
                _logger.LogDebug("Dropped event for a slow/closed SSE subscriber.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }
}

