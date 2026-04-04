# Copilot Instructions for uk.osric.copilot

## Build, test, and format

```bash
# Build (solution)
dotnet build uk.osric.copilot.slnx
# or
make build

# Run all tests
dotnet test src/uk.osric.copilot.Tests/

# Run a single test class or feature by name
dotnet test src/uk.osric.copilot.Tests/ --filter "FullyQualifiedName~CertificateServiceTests"
dotnet test src/uk.osric.copilot.Tests/ --filter "FullyQualifiedName~EmailRouting"

# Format (C# only — applies .editorconfig rules)
dotnet format src/uk.osric.copilot/uk.osric.copilot.csproj
# or
make format

# Run locally (requires `gh copilot server` to be running separately)
make run
```

To add an EF Core migration:
```bash
dotnet ef migrations add <Name> --project src/uk.osric.copilot/uk.osric.copilot.csproj
```

## Architecture

```
Browser ──HTTP/SSE──► ASP.NET Core (Kestrel, port 5000)
                          │
                    CopilotService (IHostedService)
                          │
                    GitHub.Copilot.SDK  ◄──► gh copilot server (CLI)
                          │
                    SQLite (EF Core via IDbContextFactory)
```

**Inbound email pipeline** (optional — runs when IMAP config is present):
```
ImapListenerService (IDLE/QRESYNC) ──► Channel<MimeMessage> ──► EmailProcessorService
     (MailKit, IMAP RFC 7162)           (bounded, DropWrite)     (S/MIME verify → route to session)
                                                                        │
                                                              CopilotOutboundEmailService
                                                              (watches SSE, sends SMTP replies)
```

**Request lifecycle:**
- All session events, prompts, and SDK responses are persisted as `SessionMessage` rows with a monotonic `Id`.
- `SseBroadcaster` fan-outs every event to all connected SSE clients (`GET /events`).
- On SSE reconnect, `Last-Event-ID` triggers replay of missed persisted messages before draining the live channel.
- Synthetic/service-level events (e.g. `ServiceReady`, `SessionCreated`) are broadcast with `null` Id and are **not** persisted.
- The `UserInputHandler` pauses a session and fans out a `UserInputRequested` SSE event; `POST /user-input-reply` unblocks it.

**Key services and their roles:**
| Class | Role |
|---|---|
| `CopilotService` | Owns `CopilotClient`, all active `CopilotSession` objects, and pending user-input `TaskCompletionSource` state |
| `SseBroadcaster` | Thread-safe fan-out: each SSE connection gets its own unbounded `Channel<SseMessage>` |
| `SessionRepository` | EF Core data access — always obtained via `IDbContextFactory<CopilotDbContext>` |
| `EmailProcessorService` | Drains `ChannelReader<MimeMessage>`, verifies S/MIME signature, routes to session |
| `CertificateService` | Manages `EmailCertificate` records (keyed by SHA-256 fingerprint, not serial number) |
| `CopilotMetrics` / `EmailMetrics` | OpenTelemetry `Meter` singletons; Prometheus endpoint at `/metrics` |

**DI wiring** lives entirely in `Web/AppServiceExtensions.cs` (`AddCopilotServices`). `CopilotService` is registered as both a concrete singleton and a hosted service so it can be resolved directly.

**Configuration** is nested under the `"Copilot"` key and bound to `CopilotOptions`:
```json
{
  "Copilot": {
    "DatabasePath": "copilot-sessions.db",
    "CopilotUrl": "",
    "ProjectFoldersPath": "",
    "Email": {
      "FromAddress": "",
      "ChannelCapacity": 16,
      "Imap": { "Host": "", "Port": 0, "Username": "", "Password": "", "Tls": null, "IdleTimeoutMinutes": 27 },
      "Smtp": { "Host": "", "Port": 0, "Username": "", "Password": "", "Tls": null }
    }
  }
}
```
OTLP tracing is enabled only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set in the environment.

## C# conventions

- **Block-style namespaces** (not file-scoped). `using` directives go **inside** the namespace block.
- **K&R brace style** — opening brace on the same line as the statement or declaration.
- **Braces are never optional** — all `if`/`for`/`foreach`/`while` bodies must use braces (enforced as `:error`).
- **Primary constructors** are preferred over field-initialised constructors.
- **Extension methods** are the preferred way to group related operations on a type (e.g. `builder.AddCopilotServices()`, `app.MapCopilotApi()`).
- `DateTimeOffset` values are stored in SQLite as round-trip ISO 8601 strings (`"O"` format).
- `InternalsVisibleTo("uk.osric.copilot.Tests")` is declared in the main project so tests can access internal members.
- All JSON serialisation uses `JsonNamingPolicy.CamelCase`; use that policy when constructing `JsonSerializerOptions` by hand.
- `ActivitySource` named `"uk.osric.copilot"` is used for distributed tracing spans.
- **Package versions** are managed centrally in `Directory.Packages.props` (CPM). Do not add `Version` attributes to `<PackageReference>` elements in csproj files.
- **Build artifacts** go to `artifacts/` at the solution root (`UseArtifactsOutput=true`); `bin/` and `obj/` inside project folders are not used.

## JavaScript conventions (wwwroot)

- ES modules (`import`/`export`) only — no CommonJS.
- Single quotes for string literals; template literals for interpolation.
- Semicolons at the end of every statement.
- `const` for bindings that are never reassigned, `let` for mutable, never `var`.
- JSDoc comments on every exported function.
- K&R brace style (opening brace on the same line).
- 2-space indent (per `.editorconfig`).

## Testing

Tests live in `src/uk.osric.copilot.Tests/`. There are two styles:

- **BDD (Reqnroll/NUnit):** `.feature` files under `Features/` describe behaviour in Gherkin; step bindings are in `Features/Steps/`. Use this for end-to-end service behaviour (e.g. email routing scenarios).
- **Unit (NUnit):** Plain NUnit tests under `Unit/` for isolated service logic.

`TestDbContextFactory` in `Helpers/` provides an in-memory SQLite context for tests that need the database.

Before changing production code in an existing service, add Reqnroll feature scenarios or NUnit tests to lock the current behaviour first.
