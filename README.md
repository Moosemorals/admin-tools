# admin-tools

A collection of tools to make life easier.

---

## uk.osric.copilot

A self-hosted web UI that wraps the [GitHub Copilot CLI](https://docs.github.com/en/copilot/github-copilot-in-the-cli/about-github-copilot-in-the-cli) and lets you chat with GitHub Copilot through a browser tab instead of a terminal.

> **Built by GitHub Copilot** — the code in `src/uk.osric.copilot` was written entirely by GitHub Copilot (via the Copilot CLI SDK), which is a satisfying kind of recursion.

### How it works

```
Browser ──HTTP/SSE──► ASP.NET Core (Kestrel, port 5000)
                          │
                    CopilotService (IHostedService)
                          │
                    GitHub.Copilot.SDK  ◄──► gh copilot server (CLI)
                          │
                    SQLite (Microsoft.Data.Sqlite)
                     sessions.db
```

**`CopilotService`** is a long-running hosted service that owns a single `CopilotClient` and any number of named `CopilotSession` objects.  It:

- Starts the Copilot client on application startup and resumes any sessions that were active last time the process ran (session IDs are persisted in SQLite).
- Manages create / delete / send-prompt / list operations on sessions.
- Converts every SDK `SessionEvent` into a small JSON object and broadcasts it to all connected browser tabs over [Server-Sent Events](https://developer.mozilla.org/en-US/docs/Web/API/Server-sent_events) (`GET /events`).
- Pauses when the Copilot CLI asks for user input and fans the question out as a `UserInputRequested` SSE event; the browser replies via `POST /user-input-reply` to unblock the CLI.

**The frontend** (`wwwroot/index.html`) is a single-page app that:

- Subscribes to the SSE stream and renders each session's conversation in real time.
- Lets you create new sessions, switch between them, type prompts, and answer user-input prompts from the CLI.
- Loads existing message history from `GET /sessions/{sessionId}/messages` when you switch to a session, so history survives page reloads.

**SQLite** stores session IDs and titles so sessions survive process restarts.  The database file path is configurable (default: `copilot-sessions.db` next to the binary, or `/data/copilot-sessions.db` inside the container).

### REST API

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/events` | SSE stream of all session events |
| `GET` | `/sessions` | List all sessions |
| `POST` | `/sessions` | Create a new session |
| `GET` | `/sessions/{id}/messages` | Full message history for a session |
| `POST` | `/sessions/{id}/send` | Send a prompt (plain-text body) |
| `DELETE` | `/sessions/{id}` | Delete a session |
| `POST` | `/user-input-reply` | Answer a pending user-input request |

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [GitHub CLI](https://cli.github.com/) with Copilot extension installed and authenticated (`gh auth login`)

### Running locally (development)

**1. Start the Copilot CLI in server mode**

```bash
make start-copilot
# or: gh copilot server
```

The CLI will print a URL like `http://127.0.0.1:PORT`.  Copy it.

**2. Configure the URL** (optional — the SDK will find the CLI automatically if you skip this)

```json
// src/uk.osric.copilot/appsettings.json
{
  "CopilotUrl": "http://127.0.0.1:<PORT>"
}
```

**3. Run the web server**

```bash
make run
# or: dotnet run --project src/uk.osric.copilot/uk.osric.copilot.csproj
```

Open `http://localhost:5000` in your browser.

### Building

```bash
make build
# or: dotnet build src/uk.osric.copilot/uk.osric.copilot.csproj
```

### Running in a container (production)

The project publishes a container image to `registry.osric.uk/uk.osric.copilot:latest`.

**Build and push the image**

```bash
make publish   # builds Release image, pushes :latest and a date tag
```

**Run with Podman Quadlet (systemd)**

Copy `quadlet/uk.osric.copilot.container` to your systemd container unit directory:

```bash
# rootless (per-user)
cp quadlet/uk.osric.copilot.container ~/.config/containers/systemd/

systemctl --user daemon-reload
systemctl --user start uk.osric.copilot.service
```

The unit:
- Mounts `~/.local/share/uk.osric.copilot` into the container at `/data` so the SQLite database persists across container restarts.
- Publishes port `5000` on the host.
- Sets `DatabasePath=/data/copilot-sessions.db` and leaves `CopilotUrl` blank so the SDK auto-discovers the CLI.
- Enables `AutoUpdate=registry` so `podman auto-update` will pull new images automatically.

### Configuration

Both environment variables and `appsettings.json` keys are supported (ASP.NET Core configuration layering).

| Key | Default | Description |
|-----|---------|-------------|
| `DatabasePath` | `copilot-sessions.db` | Path to the SQLite database file |
| `CopilotUrl` | *(empty)* | URL of a running `gh copilot server` process; leave blank to auto-discover |
