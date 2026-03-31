# GitMigrator

A .NET 10 CLI tool that consolidates git repositories from **GitHub**, **GitLab**
(cloud or self-hosted), and **LAN machines** into a single local folder hierarchy.

## Features

| Feature | Detail |
|---------|--------|
| **Multi-source** | GitHub, GitLab (including self-hosted), arbitrary LAN machines via SSH |
| **Flat layout** | All repos cloned to `targetFolder/<basename>/` – one level, no nested source/owner subfolders |
| **Git-history grouping** | Compares all commit SHAs across clones; repos sharing any commit are merged into one canonical folder |
| **Single folder per repo** | Copies/clones/forks from multiple sources end up as one local clone with all branches inside |
| **Resumable** | A JSON state file tracks every completed step; interrupt and restart at any time |
| **Safe** | Never force-pushes, never deletes branches or commits; all diverged code is preserved |
| **Remote wiring** | After grouping, remotes are updated to point to local clones where available |
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
├── myapp/                       ← flat: basename of the source repo path
├── scripts/
├── project-b/
└── unrelated-tool/
```

Repos from GitHub, GitLab, and LAN machines all end up as direct children of
`targetFolder`, named by their basename (last path segment, `.git` suffix stripped).

### Name collisions

If two repos from different sources share the same basename but have **different git
histories** (confirmed by the grouping phase), they are kept as separate folders:
`myapp/` and `myapp-2/`, `myapp-3/`, etc.

### Repos sharing git history

If the grouping phase determines that two repos share any git commit (they are the
same repository, a fork, or a clone), they are **merged into a single folder**:

- The most authoritative source (GitHub &gt; GitLab &gt; LAN) becomes the canonical clone.
- All other copies are fetched into the canonical clone (their branches appear as
  `merged-N/<branch>`).
- The redundant local clone directories are removed.
- Each merged source remains reachable as a `merged-N` remote inside the canonical
  clone, pointing back to the original URL on that source machine.

---

## How remote wiring works

After all clones are complete and groups are merged, the tool scans every canonical
clone's remotes.

| Scenario | Result |
|----------|--------|
| Remote URL matches a locally cloned repo | Remote URL updated to local filesystem path |
| LAN repo had its own remotes pointing to now-local repos | `local-<name>` remote added |
| Fork of a locally cloned parent | `upstream` remote added pointing to parent's local path |
| Remote URL does **not** match any local clone | Left unchanged |

No remote is ever deleted.

### Grouping remotes (`merged-N`)

When two source repos are merged into one canonical clone, the non-canonical source
gets a `merged-N` remote in the canonical clone.  Its URL points to the original
source location (e.g. the LAN machine's SSH URL) so you can continue to
`git fetch merged-N` from there directly.

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
