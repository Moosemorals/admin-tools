using System.Threading.Channels;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot.SDK;

namespace CopilotWrapper.Services;

/// <summary>
/// Hosted service that manages a GitHub Copilot CLI client and session,
/// broadcasting session events to all connected SSE subscribers.
/// </summary>
public sealed class CopilotService : IHostedService, IAsyncDisposable
{
    private readonly ILogger<CopilotService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    private CopilotClient? _client;
    private CopilotSession? _session;
    private IDisposable? _eventSubscription;

    private readonly List<Channel<string>> _subscribers = [];
    private readonly Lock _subscriberLock = new();

    public CopilotService(ILogger<CopilotService> logger)
    {
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Copilot client…");

        _client = new CopilotClient();
        await _client.StartAsync();

        _logger.LogInformation("Creating Copilot session…");

        _session = await _client.CreateSessionAsync(new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            OnUserInputRequest = async (request, _) =>
            {
                        // Surface user-input requests as a synthetic event so the front-end can see them.
                BroadcastRaw(BuildEventJson("UserInputRequested", new
                {
                    question = request.Question,
                    choices = request.Choices,
                    allowFreeform = request.AllowFreeform,
                }));
                // Auto-respond with an empty string so the session is not blocked.
                return new UserInputResponse { Answer = string.Empty, WasFreeform = true };
            },
        });

        // Subscribe to all session events.
        _eventSubscription = _session.On(OnSessionEvent);

        _logger.LogInformation("Copilot session ready (id={SessionId}).", _session.SessionId);

        BroadcastRaw(BuildEventJson("ServiceReady", new { sessionId = _session.SessionId }));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Copilot client…");
        _eventSubscription?.Dispose();
        if (_session is not null)
            await _session.DisposeAsync();
        if (_client is not null)
            await _client.StopAsync();
    }

    /// <summary>Sends a prompt to the active Copilot session.</summary>
    public async Task<string> SendAsync(string prompt)
    {
        if (_session is null)
            throw new InvalidOperationException("Session is not yet available.");

        return await _session.SendAsync(new MessageOptions { Prompt = prompt });
    }

    /// <summary>Subscribes to the event stream. Caller disposes the reader's backing channel when done.</summary>
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

    // ── Internals ────────────────────────────────────────────────────────────

    private void OnSessionEvent(SessionEvent evt)
    {
        try
        {
            // Serialize using the concrete runtime type so all properties are included.
            BroadcastRaw(BuildEventJson(evt.GetType().Name, evt, evt.GetType()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to serialize session event {Type}.", evt.GetType().Name);
        }
    }

    private string BuildEventJson(string eventType, object payload, Type? runtimeType = null)
    {
        var node = JsonSerializer.SerializeToNode(payload, runtimeType ?? payload.GetType(), _jsonOptions)
                   ?? new JsonObject();
        node["_eventType"] = eventType;
        node["_timestamp"] = DateTimeOffset.UtcNow.ToString("O");
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
        _eventSubscription?.Dispose();
        if (_session is not null)
            await _session.DisposeAsync();
        if (_client is not null)
            await _client.DisposeAsync();
    }
}
