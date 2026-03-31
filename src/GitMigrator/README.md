# GitMigrator

A .NET 10 CLI tool that consolidates git repositories from **GitHub**, **GitLab**
(cloud or self-hosted), and **LAN machines** into a single local folder hierarchy.

## Features

| Feature | Detail |
|---------|--------|
| **Multi-source** | GitHub, GitLab (including self-hosted), arbitrary LAN machines via SSH |
| **Resumable** | A JSON state file tracks every completed step; interrupt and restart at any time |
| **Safe** | Never force-pushes, never deletes branches or commits; all diverged code is preserved |
| **Remote wiring** | After cloning, remotes that pointed to remote hosts are updated to point to the local clones |
| **Fork support** | Adds an `upstream` remote in every fork that points to the local parent clone |
| **LAN source remotes** | Remotes from the source LAN repo are exposed as `local-<name>` remotes in the local clone |
| **Dry-run** | `--dry-run` logs everything that *would* happen without touching the filesystem |

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- `git` on `PATH`
- `ssh` on `PATH` (for LAN sources; passwordless/key-based auth required)
- GitHub personal access token with `repo` scope (for private repos)
- GitLab personal access token with `read_api` + `read_repository` scopes

---

## Quick start

```bash
# 1. Build
cd src/GitMigrator
dotnet build -c Release

# 2. Create your config
cp config.example.json config.json
# edit config.json – fill in your token(s), target folder, and sources

# Protect the file – it contains your API tokens
chmod 600 config.json

# 3. Run (first time)
dotnet run --project src/GitMigrator -- config.json

# 4. If interrupted, simply run again – already-completed work is skipped
dotnet run --project src/GitMigrator -- config.json
```

Or publish a self-contained binary:

```bash
dotnet publish src/GitMigrator -c Release -r linux-x64 --self-contained -o dist
./dist/GitMigrator config.json
```

---

## Usage

```
GitMigrator [options] [config.json]

Arguments:
  config.json           Path to the JSON config file (default: ./config.json)

Options:
  --dry-run             Print what would happen without cloning or changing remotes
  --verbose             Enable debug-level log output
  --reset-discovery     Re-query all sources even if discovery was already done
  --reset-failed        Retry repos whose clone previously failed
  --reset-all           Start fresh (clears all saved progress)
  --help, -h            Show help
```

---

## Configuration

Copy `config.example.json` to `config.json` and edit it.

```jsonc
{
  "targetFolder": "/home/alice/repos",   // where repos are cloned
  "dryRun": false,
  "preferSsh": false,                    // true = use SSH URLs (requires SSH keys for GitHub/GitLab)
  "sources": [
    {
      "type": "github",
      "token": "ghp_...",
      "users": ["alice"],
      "orgs":  ["my-company"]
    },
    {
      "type": "gitlab",
      "token": "glpat-...",
      "baseUrl": "https://gitlab.com",
      "users":  ["alice"],
      "groups": ["my-group"]
    },
    {
      "type": "lan",
      "host": "devbox",
      "user": "alice",
      "repos": ["/home/alice/myproject"],
      "scanPaths": ["/home/alice/projects"],
      "maxScanDepth": 5
    }
  ]
}
```

### Security note

`config.json` contains API tokens. Set restrictive permissions:

```bash
chmod 600 config.json
```

Tokens embedded in HTTPS clone URLs are **stripped from `.git/config`** immediately after each
clone, so they are not persisted in the repository.

---

## Output folder layout

```
/home/alice/repos/
├── .migration-state.json        ← resume state (do not delete while migrating)
├── github.com/
│   └── alice/
│       ├── project-a/          ← regular git clone
│       └── project-b/
├── gitlab.com/
│   └── alice/
│       └── project-c/
└── lan/
    └── devbox/
        └── home/
            └── alice/
                └── myproject/
```

---

## How remote wiring works

After all clones are complete the tool scans every local clone's remotes.

| Scenario | Result |
|----------|--------|
| Remote URL matches a locally cloned repo | Remote URL updated to local filesystem path |
| LAN repo had its own remotes pointing to now-local repos | `local-<name>` remote added |
| Fork of a locally cloned parent | `upstream` remote added pointing to parent's local path |
| Remote URL does **not** match any local clone | Left unchanged |

No remote is ever deleted.

---

## Handling diverged branches

If a LAN repo has local commits not pushed to its remote, those commits are preserved
in the LAN clone at `lan/<host>/<path>`. The tool does **not** merge or rebase; that
decision is left to you.

---

## State file

The state file (`.migration-state.json` by default) stores:
- The list of discovered repos and their planned local paths
- The clone status of each repo (`Pending`, `Complete`, `Failed`)
- The planned and completed remote update operations

If the migration is interrupted, re-running the tool reads the state file and skips
everything that was already completed. Use `--reset-failed` to retry failed clones,
or `--reset-all` to start from scratch.
