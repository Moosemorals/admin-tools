namespace uk.osric.copilot.Services {
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using uk.osric.copilot.Data;
    using uk.osric.copilot.Models;
    using GitHub.Copilot.SDK;

    /// <summary>
    /// Hosted service that manages a GitHub Copilot CLI client and multiple sessions,
    /// persisting all events to the database and broadcasting them to SSE subscribers.
    /// </summary>
    public sealed class CopilotService(
        ILogger<CopilotService> logger,
        SessionRepository db,
        SseBroadcaster broadcaster,
        string? copilotUrl = null) : IHostedService, IAsyncDisposable {

        // ── Per-session state ─────────────────────────────────────────────────

        private sealed class SessionState(CopilotSession session, IDisposable subscription) {
            public CopilotSession Session { get; } = session;
            public IDisposable Subscription { get; } = subscription;

            /// <summary>Pending user-input requests keyed by our generated requestId.</summary>
            public Dictionary<string, TaskCompletionSource<UserInputResponse>> PendingInputs { get; } = new();
            public Lock PendingInputsLock { get; } = new();
        }

        // ── Fields ────────────────────────────────────────────────────────────

        private readonly JsonSerializerOptions _jsonOptions = new() {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private CopilotClient? _client;

        private readonly Dictionary<string, SessionState> _sessions = new();
        private readonly Lock _sessionsLock = new();

        // ── IHostedService ────────────────────────────────────────────────────

        public async Task StartAsync(CancellationToken cancellationToken) {
            logger.LogInformation("Starting Copilot client…");
            var options = string.IsNullOrWhiteSpace(copilotUrl)
                ? new CopilotClientOptions()
                : new CopilotClientOptions { CliUrl = copilotUrl };
            _client = new CopilotClient(options);
            await _client.StartAsync(cancellationToken);

            // Resume all sessions previously created in this UI.
            var stored = await db.GetAllAsync();
            foreach (var record in stored) {
                try {
                    await ResumeSessionCoreAsync(record.Id, record.WorkingDirectory, cancellationToken);
                    logger.LogInformation("Resumed session {Id}.", record.Id);
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Could not resume session {Id}; removing from storage.", record.Id);
                    await db.DeleteAsync(record.Id);
                }
            }

            logger.LogInformation("Copilot service ready.");
            // ServiceReady is a synthetic event with no session — not persisted.
            broadcaster.Broadcast(new SseMessage(null, BuildEventJson("ServiceReady", new { })));
        }

        public async Task StopAsync(CancellationToken cancellationToken) {
            logger.LogInformation("Stopping Copilot service…");

            List<SessionState> snapshot;
            lock (_sessionsLock) {
                snapshot = [.. _sessions.Values];
                _sessions.Clear();
            }

            foreach (var state in snapshot) {
                CancelPendingInputs(state);
                state.Subscription.Dispose();
                await state.Session.DisposeAsync();
            }

            if (_client is not null) {
                await _client.StopAsync();
            }
        }

        // ── Session management ────────────────────────────────────────────────

        /// <summary>Creates a new Copilot session, persists it, and broadcasts <c>SessionCreated</c>.</summary>
        public async Task<Session> CreateSessionAsync(string? workingDirectory = null) {
            if (_client is null) {
                throw new InvalidOperationException("Copilot client is not running.");
            }

            var sessionId = Guid.NewGuid().ToString("N");
            var session = await _client.CreateSessionAsync(new SessionConfig {
                SessionId = sessionId,
                OnPermissionRequest = PermissionHandler.ApproveAll,
                OnUserInputRequest = BuildUserInputHandler(sessionId),
                WorkingDirectory = workingDirectory,
            });

            var now = DateTimeOffset.UtcNow;
            var title = $"Session {now:HH:mm:ss}";
            var record = new Session {
                Id = sessionId,
                Title = title,
                CreatedAt = now,
                LastActiveAt = now,
                WorkingDirectory = workingDirectory,
            };

            await db.UpsertAsync(record);
            RegisterSession(sessionId, session);

            logger.LogInformation("Created session {Id}.", sessionId);
            // SessionCreated is a sidebar event — not persisted as a message.
            broadcaster.Broadcast(new SseMessage(null, BuildEventJson("SessionCreated", new {
                sessionId,
                title,
                createdAt = record.CreatedAt,
                lastActiveAt = record.LastActiveAt,
                workingDirectory,
            })));

            return record;
        }

        public Task<IReadOnlyList<Session>> ListSessionsAsync() => db.GetAllAsync();

        /// <summary>
        /// Returns the stored event history for a session with Id > <paramref name="afterId"/>,
        /// serialised with <c>_id</c> injected so the frontend can render them identically to
        /// live SSE events.
        /// </summary>
        public async Task<IReadOnlyList<JsonElement>> GetMessagesJsonAsync(string sessionId, long afterId = 0) {
            var messages = await db.GetMessagesAfterAsync(sessionId, afterId);
            return messages.Select(m => AddId(m.Payload, m.Id, _jsonOptions)).ToList();
        }

        /// <summary>
        /// Returns all stored events across all sessions with Id > <paramref name="afterId"/>,
        /// as (id, json) tuples suitable for SSE replay on reconnect.
        /// </summary>
        public async Task<IReadOnlyList<(long Id, string Json)>> GetEventsAfterAsync(long afterId) {
            var messages = await db.GetAllEventsAfterAsync(afterId);
            return messages.Select(m => (m.Id, AddId(m.Payload, m.Id, _jsonOptions).GetRawText())).ToList();
        }

        /// <summary>Sends a prompt to the specified session and stores it as a UserMessage event.</summary>
        public async Task<string> SendAsync(string sessionId, string prompt) {
            var state = GetState(sessionId);
            // Store the user's message before sending so it's in the history.
            await StoreAndBroadcastAsync("UserMessage", new { prompt }, sessionId: sessionId);
            var msgId = await state.Session.SendAsync(new MessageOptions { Prompt = prompt });
            await db.TouchAsync(sessionId, DateTimeOffset.UtcNow);
            return msgId;
        }

        /// <summary>Deletes a session from memory, the SDK, and the database.</summary>
        public async Task DeleteSessionAsync(string sessionId) {
            SessionState? state;
            lock (_sessionsLock) {
                _sessions.Remove(sessionId, out state);
            }

            if (state is not null) {
                CancelPendingInputs(state);
                state.Subscription.Dispose();
                await state.Session.DisposeAsync();
            }

            if (_client is not null) {
                await _client.DeleteSessionAsync(sessionId);
            }

            await db.DeleteAsync(sessionId);
            logger.LogInformation("Deleted session.");
        }

        /// <summary>
        /// Completes a pending user-input request and stores a <c>UserInputReply</c> event.
        /// Returns <c>true</c> if found, <c>false</c> if the requestId is unknown.
        /// </summary>
        public bool ReplyUserInput(string requestId, string answer, bool wasFreeform) {
            List<(string SessionId, SessionState State)> snapshot;
            lock (_sessionsLock) {
                snapshot = [.. _sessions.Select(kvp => (kvp.Key, kvp.Value))];
            }

            foreach (var (sessionId, state) in snapshot) {
                TaskCompletionSource<UserInputResponse>? tcs;
                lock (state.PendingInputsLock) {
                    if (!state.PendingInputs.Remove(requestId, out tcs)) {
                        continue;
                    }
                }

                tcs.TrySetResult(new UserInputResponse { Answer = answer, WasFreeform = wasFreeform });
                // Store the reply event (fire-and-forget; do not block the HTTP response).
                _ = StoreAndBroadcastAsync("UserInputReply",
                    new { requestId, answer, wasFreeform },
                    sessionId: sessionId);
                return true;
            }

            return false;
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private async Task ResumeSessionCoreAsync(
                string sessionId,
                string? workingDirectory,
                CancellationToken cancellationToken) {
            var session = await _client!.ResumeSessionAsync(sessionId, new ResumeSessionConfig {
                OnPermissionRequest = PermissionHandler.ApproveAll,
                OnUserInputRequest = BuildUserInputHandler(sessionId),
                WorkingDirectory = workingDirectory,
            }, cancellationToken);

            RegisterSession(sessionId, session);
        }

        private void RegisterSession(string sessionId, CopilotSession session) {
            var sub = session.On(evt => OnSessionEvent(sessionId, evt));
            lock (_sessionsLock) {
                _sessions[sessionId] = new SessionState(session, sub);
            }
        }

        private UserInputHandler BuildUserInputHandler(string sessionId) =>
            async (request, _) => {
                var requestId = Guid.NewGuid().ToString("N");
                var tcs = new TaskCompletionSource<UserInputResponse>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                SessionState? state;
                lock (_sessionsLock) {
                    _sessions.TryGetValue(sessionId, out state);
                }

                if (state is null) {
                    return new UserInputResponse { Answer = string.Empty, WasFreeform = true };
                }

                lock (state.PendingInputsLock) {
                    state.PendingInputs[requestId] = tcs;
                }

                await StoreAndBroadcastAsync("UserInputRequested", new {
                    requestId,
                    question = request.Question,
                    choices = request.Choices,
                    allowFreeform = request.AllowFreeform,
                }, sessionId: sessionId);

                try {
                    return await tcs.Task;
                } catch (OperationCanceledException) {
                    lock (state.PendingInputsLock) {
                        state.PendingInputs.Remove(requestId);
                    }
                    return new UserInputResponse { Answer = string.Empty, WasFreeform = true };
                }
            };

        /// <summary>Synchronous callback from the SDK event subscription.</summary>
        private void OnSessionEvent(string sessionId, SessionEvent evt) {
            // Fire-and-forget; errors are logged inside StoreAndBroadcastAsync.
            _ = StoreAndBroadcastAsync(evt.GetType().Name, evt, evt.GetType(), sessionId);
        }

        /// <summary>
        /// Builds the JSON payload, persists it to the database (if a sessionId is
        /// provided), injects the generated <c>_id</c> field, then broadcasts the
        /// final message to all SSE subscribers.
        /// </summary>
        private async Task StoreAndBroadcastAsync(
                string eventType,
                object payload,
                Type? runtimeType = null,
                string? sessionId = null) {
            try {
                var node = JsonSerializer.SerializeToNode(
                    payload, runtimeType ?? payload.GetType(), _jsonOptions) ?? new JsonObject();
                node["_eventType"] = eventType;
                node["_timestamp"] = DateTimeOffset.UtcNow.ToString("O");
                if (sessionId is not null) {
                    node["_sessionId"] = sessionId;
                }

                long? messageId = null;
                if (sessionId is not null) {
                    var intermediateJson = node.ToJsonString(_jsonOptions);
                    var msg = new SessionMessage {
                        SessionId = sessionId,
                        EventType = eventType,
                        Payload = intermediateJson,
                        CreatedAt = DateTimeOffset.UtcNow,
                    };
                    try {
                        await db.AddMessageAsync(msg);
                        messageId = msg.Id;
                        node["_id"] = messageId;
                    } catch (Exception ex) {
                        logger.LogWarning(ex,
                            "Failed to persist event {EventType} for session {SessionId}.",
                            eventType, sessionId);
                    }
                }

                broadcaster.Broadcast(new SseMessage(messageId, node.ToJsonString(_jsonOptions)));
            } catch (Exception ex) {
                logger.LogWarning(ex, "Failed to build/broadcast event {EventType}.", eventType);
            }
        }

        private SessionState GetState(string sessionId) {
            lock (_sessionsLock) {
                if (_sessions.TryGetValue(sessionId, out var state)) {
                    return state;
                }
            }
            throw new KeyNotFoundException($"Session '{sessionId}' not found.");
        }

        private static void CancelPendingInputs(SessionState state) {
            lock (state.PendingInputsLock) {
                foreach (var tcs in state.PendingInputs.Values) {
                    tcs.TrySetCanceled();
                }
                state.PendingInputs.Clear();
            }
        }

        private string BuildEventJson(string eventType, object payload) {
            var node = JsonSerializer.SerializeToNode(payload, payload.GetType(), _jsonOptions)
                       ?? new JsonObject();
            node["_eventType"] = eventType;
            node["_timestamp"] = DateTimeOffset.UtcNow.ToString("O");
            return node.ToJsonString(_jsonOptions);
        }

        /// <summary>
        /// Parses a stored JSON payload, injects the <c>_id</c> field, and returns
        /// a clone of the root element ready for API serialisation.
        /// </summary>
        private static JsonElement AddId(string payload, long id, JsonSerializerOptions options) {
            var node = JsonNode.Parse(payload)!;
            node["_id"] = id;
            return JsonDocument.Parse(node.ToJsonString(options)).RootElement.Clone();
        }

        public async ValueTask DisposeAsync() {
            await StopAsync(CancellationToken.None);
        }
    }
}

