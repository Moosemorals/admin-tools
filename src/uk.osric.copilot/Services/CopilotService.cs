namespace uk.osric.copilot.Services {
    using System.Diagnostics;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using uk.osric.copilot.Data;
    using uk.osric.copilot.Models;
    using GitHub.Copilot.SDK;

    /// <summary>
    /// Hosted service that owns the GitHub Copilot CLI client and all active sessions.
    /// All Copilot SDK events, user prompts, and input replies are persisted to the database
    /// and broadcast to SSE subscribers via <see cref="SseBroadcaster"/>.
    /// </summary>
    public sealed class CopilotService(
            ILogger<CopilotService> logger,
            SessionRepository db,
            SseBroadcaster broadcaster,
            CopilotMetrics metrics,
            SmtpSenderService? smtpSender = null,
            string? copilotUrl = null) : IHostedService, IAsyncDisposable {

        // ── Per-session state ─────────────────────────────────────────────────

        private sealed class SessionState(CopilotSession session, IDisposable subscription, string? emailAddress, string? inboundMessageId) {
            public CopilotSession Session { get; } = session;
            public IDisposable Subscription { get; } = subscription;
            public string? EmailAddress { get; } = emailAddress;
            public string? InboundMessageId { get; } = inboundMessageId;

            /// <summary>Pending user-input requests keyed by our generated requestId.</summary>
            public Dictionary<string, TaskCompletionSource<UserInputResponse>> PendingInputs { get; } = new();
            public Lock PendingInputsLock { get; } = new();
        }

        private static readonly ActivitySource _activitySource = new("uk.osric.copilot");

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

            // Resume all sessions persisted from a previous run so the client can pick up
            // where it left off after a restart.
            var stored = await db.GetAllAsync();
            foreach (var record in stored) {
                try {
                    await ResumeSessionCoreAsync(record.Id, record.WorkingDirectory, record.EmailAddress, cancellationToken, record.InboundMessageId);
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

        /// <summary>
        /// Creates a new Copilot session, persists it to the database, and broadcasts a
        /// <c>SessionCreated</c> event so connected clients add it to their sidebar.
        /// </summary>
        internal async Task<Session> CreateSessionAsync(string? workingDirectory = null, string? emailAddress = null, string? inboundMessageId = null) {
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
                EmailAddress = emailAddress,
                InboundMessageId = inboundMessageId,
            };

            await db.UpsertAsync(record);
            RegisterSession(sessionId, session, emailAddress, inboundMessageId);

            metrics.IncrementSessionsTotal();
            metrics.IncrementSessionsActive();
            logger.LogInformation("Created session {Id}.", sessionId);
            // SessionCreated drives sidebar insertion on the client — not persisted as a message
            // because it is not part of the session's conversation history.
            broadcaster.Broadcast(new SseMessage(null, BuildEventJson("SessionCreated", new {
                sessionId,
                title,
                createdAt = record.CreatedAt,
                lastActiveAt = record.LastActiveAt,
                workingDirectory,
            })));

            return record;
        }

        /// <summary>Returns all persisted sessions ordered by most-recently active.</summary>
        internal Task<IReadOnlyList<Session>> ListSessionsAsync() => db.GetAllAsync();

        /// <summary>
        /// Returns stored events for <paramref name="sessionId"/> with Id &gt;
        /// <paramref name="afterId"/>, with <c>_id</c> injected so the client can render them
        /// identically to live SSE events.
        /// Returns an empty list (not 404) when the session has no messages or is unknown —
        /// history replay is best-effort.
        /// </summary>
        internal async Task<IReadOnlyList<JsonElement>> GetMessagesJsonAsync(
                string sessionId, long afterId = 0) {
            var messages = await db.GetMessagesAfterAsync(sessionId, afterId);
            return messages.Select(m => InjectId(m.Payload, m.Id)).ToList();
        }

        /// <summary>
        /// Returns all persisted events across all sessions with Id &gt; <paramref name="afterId"/>,
        /// as (id, json) tuples.  Used to replay events missed during an SSE reconnect.
        /// </summary>
        internal async Task<IReadOnlyList<(long Id, string Json)>> GetEventsAfterAsync(long afterId) {
            var messages = await db.GetAllEventsAfterAsync(afterId);
            return messages.Select(m => (m.Id, InjectId(m.Payload, m.Id).GetRawText())).ToList();
        }

        /// <summary>
        /// Stores the user's prompt as a <c>UserMessage</c> event, then sends it to Copilot.
        /// The message is stored first so the history is consistent even if the send fails.
        /// </summary>
        internal async Task<string> SendAsync(string sessionId, string prompt) {
            using var activity = _activitySource.StartActivity("copilot.send");
            activity?.SetTag("session.id", sessionId);
            var state = GetState(sessionId);
            await StoreAndBroadcastAsync("UserMessage", new { prompt }, sessionId: sessionId);
            var msgId = await state.Session.SendAsync(new MessageOptions { Prompt = prompt });
            await db.TouchAsync(sessionId, DateTimeOffset.UtcNow);
            return msgId;
        }

        /// <summary>Removes a session from memory, the Copilot SDK, and the database.</summary>
        internal async Task DeleteSessionAsync(string sessionId) {
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
            metrics.DecrementSessionsActive();
            logger.LogInformation("Deleted session.");
        }

        /// <summary>
        /// Resolves a pending user-input request and stores a <c>UserInputReply</c> event.
        /// </summary>
        /// <returns><c>true</c> if found and resolved; <c>false</c> if the requestId is unknown.</returns>
        internal bool ReplyUserInput(string requestId, string answer, bool wasFreeform) {
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
                // Fire-and-forget: persist the reply without blocking the HTTP response.
                _ = StoreAndBroadcastAsync("UserInputReply",
                    new { requestId, answer, wasFreeform },
                    sessionId: sessionId);
                return true;
            }

            return false;
        }

        /// <summary>Returns the email address for the session, or null if not set.</summary>
        internal string? GetSessionEmailAddress(string sessionId) {
            lock (_sessionsLock) {
                return _sessions.TryGetValue(sessionId, out var state) ? state.EmailAddress : null;
            }
        }

        /// <summary>Returns the inbound message ID for the session, or null if not set.</summary>
        internal string? GetSessionInboundMessageId(string sessionId) {
            lock (_sessionsLock) {
                return _sessions.TryGetValue(sessionId, out var state) ? state.InboundMessageId : null;
            }
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private async Task ResumeSessionCoreAsync(
                string sessionId,
                string? workingDirectory,
                string? emailAddress,
                CancellationToken cancellationToken,
                string? inboundMessageId = null) {
            var session = await _client!.ResumeSessionAsync(sessionId, new ResumeSessionConfig {
                OnPermissionRequest = PermissionHandler.ApproveAll,
                OnUserInputRequest = BuildUserInputHandler(sessionId),
                WorkingDirectory = workingDirectory,
            }, cancellationToken);

            RegisterSession(sessionId, session, emailAddress, inboundMessageId);
        }

        private void RegisterSession(string sessionId, CopilotSession session, string? emailAddress = null, string? inboundMessageId = null) {
            var sub = session.On(evt => OnSessionEvent(sessionId, evt));
            lock (_sessionsLock) {
                _sessions[sessionId] = new SessionState(session, sub, emailAddress, inboundMessageId);
            }
        }

        private UserInputHandler BuildUserInputHandler(string sessionId) =>
            async (request, cancellationToken) => {
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

                if (state.EmailAddress is not null && smtpSender is not null) {
                    _ = smtpSender.SendReplyAsync(
                        state.EmailAddress,
                        "Input required",
                        $"Copilot is asking: {request.Question}\n\n(Reply via the web UI at /sessions/{sessionId})",
                        cancellationToken: CancellationToken.None);
                }

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
        /// Builds a JSON payload node populated with <c>_eventType</c>, <c>_timestamp</c>,
        /// and (if provided) <c>_sessionId</c> metadata fields.  The common base for both
        /// persisted and non-persisted events.
        /// </summary>
        private JsonObject BuildEventNode(
                string eventType, object payload, Type? runtimeType = null, string? sessionId = null) {
            var node = (JsonObject)(
                JsonSerializer.SerializeToNode(
                    payload, runtimeType ?? payload.GetType(), _jsonOptions)
                ?? new JsonObject());
            node["_eventType"] = eventType;
            node["_timestamp"] = DateTimeOffset.UtcNow.ToString("O");
            if (sessionId is not null) {
                node["_sessionId"] = sessionId;
            }
            return node;
        }

        /// <summary>Builds a JSON string for a non-persisted service-level event.</summary>
        private string BuildEventJson(string eventType, object payload) =>
            BuildEventNode(eventType, payload).ToJsonString(_jsonOptions);

        /// <summary>
        /// Builds the JSON payload, persists it (when <paramref name="sessionId"/> is set),
        /// injects the generated <c>_id</c>, then broadcasts to all SSE subscribers.
        /// Errors are caught and logged so a single bad event does not crash the event loop.
        /// </summary>
        private async Task StoreAndBroadcastAsync(
                string eventType,
                object payload,
                Type? runtimeType = null,
                string? sessionId = null) {
            try {
                var node = BuildEventNode(eventType, payload, runtimeType, sessionId);

                long? messageId = null;
                if (sessionId is not null) {
                    var msg = new SessionMessage {
                        SessionId = sessionId,
                        EventType = eventType,
                        // Persist without _id; the field is injected on read from the DB row id.
                        Payload = node.ToJsonString(_jsonOptions),
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

        /// <summary>
        /// Parses a stored JSON payload, injects the <c>_id</c> field from the DB row id,
        /// and returns a cloned element ready for HTTP serialisation.
        /// </summary>
        private JsonElement InjectId(string payload, long id) {
            var node = JsonNode.Parse(payload)!;
            node["_id"] = id;
            return JsonDocument.Parse(node.ToJsonString(_jsonOptions)).RootElement.Clone();
        }

        public async ValueTask DisposeAsync() {
            await StopAsync(CancellationToken.None);
        }
    }
}

