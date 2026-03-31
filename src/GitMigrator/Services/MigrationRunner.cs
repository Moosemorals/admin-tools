using GitMigrator.Models;
using Microsoft.Extensions.Logging;

namespace GitMigrator.Services;

/// <summary>
/// Orchestrates the three-phase migration:
///   1. Discovery  – query all sources and record every repo to the state file.
///   2. Cloning    – clone each discovered repo that has not yet been cloned.
///   3. Remoting   – update/add remotes so local clones reference each other.
///
/// The state file is written after each individual operation so the process can be
/// interrupted and resumed without repeating completed work.
/// </summary>
public class MigrationRunner
{
    private readonly MigrationConfig _cfg;
    private readonly StateManager _state;
    private readonly GitService _git;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(
        MigrationConfig cfg,
        StateManager state,
        GitService git,
        ILoggerFactory loggerFactory)
    {
        _cfg = cfg;
        _state = state;
        _git = git;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MigrationRunner>();
    }

    public async Task RunAsync()
    {
        // -----------------------------------------------------------------------
        // Phase 1: Discovery
        // -----------------------------------------------------------------------
        if (!_state.State.DiscoveryComplete)
        {
            _logger.LogInformation("═══ Phase 1: Discovering repositories ═══");
            _state.State.Phase = MigrationPhase.Discovering;
            _state.Save();

            await RunDiscoveryAsync();

            _state.State.DiscoveryComplete = true;
            _state.State.Phase = MigrationPhase.Cloning;
            _state.Save();
        }
        else
        {
            _logger.LogInformation(
                "Skipping discovery (already complete – {count} repos found). Use --reset-discovery to re-run.",
                _state.State.DiscoveredRepos.Count);
        }

        // -----------------------------------------------------------------------
        // Phase 2: Cloning
        // -----------------------------------------------------------------------
        if (_state.State.Phase is MigrationPhase.Discovering or MigrationPhase.Cloning)
        {
            _logger.LogInformation("═══ Phase 2: Cloning repositories ═══");
            await RunCloningAsync();

            _state.State.Phase = MigrationPhase.UpdatingRemotes;
            _state.Save();
        }

        // -----------------------------------------------------------------------
        // Phase 3: Remote reconciliation
        // -----------------------------------------------------------------------
        if (_state.State.Phase is MigrationPhase.Cloning or MigrationPhase.UpdatingRemotes)
        {
            _logger.LogInformation("═══ Phase 3: Updating remotes ═══");
            await RunRemoteReconciliationAsync();

            _state.State.Phase = MigrationPhase.Complete;
            _state.Save();
        }

        if (_state.State.Phase == MigrationPhase.Complete)
        {
            var cloned = _state.State.CloneStates.Values.Count(c => c.Status == CloneStatus.Complete);
            var failed = _state.State.CloneStates.Values.Count(c => c.Status == CloneStatus.Failed);
            _logger.LogInformation(
                "Migration complete. Cloned: {ok}, Failed: {fail}.",
                cloned, failed);
        }
    }

    // =========================================================================
    // Phase 1: Discovery
    // =========================================================================

    private async Task RunDiscoveryAsync()
    {
        foreach (var source in _cfg.Sources)
        {
            List<RepoInfo> discovered;

            try
            {
                discovered = source switch
                {
                    GitHubSourceConfig ghCfg => await new GitHubDiscovery(
                        ghCfg,
                        new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                        _loggerFactory.CreateLogger<GitHubDiscovery>(),
                        _cfg.TargetFolder).DiscoverReposAsync(),

                    GitLabSourceConfig glCfg => await new GitLabDiscovery(
                        glCfg,
                        new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                        _loggerFactory.CreateLogger<GitLabDiscovery>(),
                        _cfg.TargetFolder).DiscoverReposAsync(),

                    LanSourceConfig lanCfg => await new LanDiscovery(
                        lanCfg,
                        _loggerFactory.CreateLogger<LanDiscovery>(),
                        _cfg.TargetFolder).DiscoverReposAsync(),

                    _ => throw new InvalidOperationException($"Unsupported source type: {source.GetType().Name}")
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discovery failed for source type '{type}'. Skipping.", source.Type);
                continue;
            }

            foreach (var repo in discovered)
            {
                // Avoid duplicates (same repo id discovered via multiple sources/users)
                if (_state.State.DiscoveredRepos.Any(r => r.Id == repo.Id))
                {
                    _logger.LogDebug("Skipping duplicate repo id: {id}", repo.Id);
                    continue;
                }
                _state.State.DiscoveredRepos.Add(repo);
            }
        }

        _logger.LogInformation("Discovery complete. Total repos: {count}.", _state.State.DiscoveredRepos.Count);
        _state.Save();
    }

    // =========================================================================
    // Phase 2: Cloning
    // =========================================================================

    private async Task RunCloningAsync()
    {
        var repos = _state.State.DiscoveredRepos;
        int total = repos.Count;
        int idx = 0;

        foreach (var repo in repos)
        {
            idx++;

            if (_state.State.CloneStates.TryGetValue(repo.Id, out var existing) &&
                existing.Status == CloneStatus.Complete)
            {
                _logger.LogInformation("[{idx}/{total}] Already cloned: {name} → {path}",
                    idx, total, repo.FullName, repo.LocalPath);
                continue;
            }

            _logger.LogInformation("[{idx}/{total}] Cloning {name} ({type})...",
                idx, total, repo.FullName, repo.SourceType);

            var (cloneUrl, cleanUrl) = BuildCloneUrls(repo);

            bool success;
            string? errorMsg = null;

            try
            {
                success = await _git.CloneAsync(cloneUrl, repo.LocalPath, cleanUrl, timeoutSeconds: 900);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error cloning {name}.", repo.FullName);
                success = false;
                errorMsg = ex.Message;
            }

            _state.State.CloneStates[repo.Id] = new RepoCloneState
            {
                RepoId = repo.Id,
                LocalPath = repo.LocalPath,
                Status = success ? CloneStatus.Complete : CloneStatus.Failed,
                Error = success ? null : (errorMsg ?? "Clone failed; see log for details."),
                CompletedAt = success ? DateTime.UtcNow : null
            };

            _state.Save(); // persist after every clone so an interrupt doesn't lose progress
        }

        int ok = _state.State.CloneStates.Values.Count(c => c.Status == CloneStatus.Complete);
        int fail = _state.State.CloneStates.Values.Count(c => c.Status == CloneStatus.Failed);
        _logger.LogInformation("Cloning complete. Succeeded: {ok}, Failed: {fail}.", ok, fail);

        if (fail > 0)
        {
            _logger.LogWarning(
                "{fail} repo(s) failed to clone. Re-run to retry them (they will be attempted again automatically).",
                fail);
        }
    }

    // =========================================================================
    // Phase 3: Remote reconciliation
    // =========================================================================

    private async Task RunRemoteReconciliationAsync()
    {
        // Build a map: normalised-URL → local-filesystem-path
        var urlToLocal = BuildUrlMap();

        if (urlToLocal.Count == 0)
        {
            _logger.LogInformation("No successfully cloned repos to reconcile remotes for.");
            return;
        }

        // Plan remote updates on first call to this phase
        if (_state.State.RemoteUpdates.Count == 0)
        {
            await PlanRemoteUpdatesAsync(urlToLocal);
            _state.Save();
        }

        if (_state.State.RemoteUpdates.Count == 0)
        {
            _logger.LogInformation("No remote updates needed.");
            return;
        }

        // Build repoId → localPath lookup (used when executing updates)
        var idToPath = _state.State.DiscoveredRepos
            .ToDictionary(r => r.Id, r => r.LocalPath, StringComparer.OrdinalIgnoreCase);

        int done = 0, skipped = 0, failed = 0;
        foreach (var update in _state.State.RemoteUpdates)
        {
            if (update.IsDone)
            {
                skipped++;
                continue;
            }

            if (!idToPath.TryGetValue(update.RepoId, out var localPath))
            {
                _logger.LogWarning("Cannot find local path for repo '{id}', skipping remote update.", update.RepoId);
                failed++;
                continue;
            }

            bool ok;
            try
            {
                ok = update.IsUpdate
                    ? await _git.SetRemoteUrlAsync(localPath, update.RemoteName, update.NewUrl)
                    : await _git.AddRemoteAsync(localPath, update.RemoteName, update.NewUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remote update failed for {repo}/{remote}.", update.RepoId, update.RemoteName);
                ok = false;
                update.Error = ex.Message;
            }

            update.IsDone = ok;
            if (ok) done++;
            else failed++;

            _state.Save();
        }

        _logger.LogInformation(
            "Remote reconciliation complete. Applied: {done}, Already done: {skipped}, Failed: {failed}.",
            done, skipped, failed);
    }

    // -------------------------------------------------------------------------
    // Remote planning helpers
    // -------------------------------------------------------------------------

    private async Task PlanRemoteUpdatesAsync(Dictionary<string, string> urlToLocal)
    {
        _logger.LogInformation("Planning remote updates...");

        foreach (var repo in _state.State.DiscoveredRepos)
        {
            if (!_state.State.CloneStates.TryGetValue(repo.Id, out var cs) ||
                cs.Status != CloneStatus.Complete)
                continue;

            var localPath = repo.LocalPath;

            // ── A. Remotes already in the local clone ──────────────────────────
            // git-clone sets 'origin' to the clone URL. If that URL (or any other
            // remote URL) corresponds to a locally cloned repo, update it to the
            // local path so the user can work fully offline.
            var currentRemotes = await _git.GetRemotesAsync(localPath);
            foreach (var (remoteName, remoteUrl) in currentRemotes)
            {
                var norm = UrlHelper.Normalize(remoteUrl);
                if (urlToLocal.TryGetValue(norm, out var localTarget) &&
                    !PathsEqual(localTarget, localPath))
                {
                    AddPlanEntry(repo.Id, remoteName, remoteUrl, localTarget, isUpdate: true);
                }
            }

            // ── B. Original remotes from the *source* repo (LAN) ───────────────
            // The local clone's origin points to the LAN machine, but that LAN
            // repo had its own remotes (e.g. origin → GitHub). Surface those as
            // 'local-{name}' so the user can fetch from local siblings directly.
            foreach (var (remoteName, remoteUrl) in repo.OriginalRemotes)
            {
                var norm = UrlHelper.Normalize(remoteUrl);
                if (!urlToLocal.TryGetValue(norm, out var localTarget))
                    continue;
                if (PathsEqual(localTarget, localPath))
                    continue;

                var newRemoteName = $"local-{remoteName}";
                bool alreadyPlanned = _state.State.RemoteUpdates.Any(u =>
                    u.RepoId == repo.Id && u.RemoteName == newRemoteName);
                bool alreadyExists = currentRemotes.ContainsKey(newRemoteName);
                if (!alreadyPlanned && !alreadyExists)
                {
                    AddPlanEntry(repo.Id, newRemoteName, remoteUrl, localTarget, isUpdate: false);
                }
            }

            // ── C. Fork parent ─────────────────────────────────────────────────
            // If this repo is a fork, add an 'upstream' remote pointing to the
            // local clone of the parent (if we have it).
            if (repo.ForkOf is { Length: > 0 } parentUrl)
            {
                var norm = UrlHelper.Normalize(parentUrl);
                if (urlToLocal.TryGetValue(norm, out var parentLocal) &&
                    !PathsEqual(parentLocal, localPath))
                {
                    const string upstreamName = "upstream";
                    bool alreadyPlanned = _state.State.RemoteUpdates.Any(u =>
                        u.RepoId == repo.Id && u.RemoteName == upstreamName);
                    bool alreadyExists = currentRemotes.ContainsKey(upstreamName);
                    if (!alreadyPlanned && !alreadyExists)
                    {
                        AddPlanEntry(repo.Id, upstreamName, parentUrl, parentLocal, isUpdate: false);
                    }
                }
            }
        }

        _logger.LogInformation("Planned {count} remote operation(s).", _state.State.RemoteUpdates.Count);
    }

    private void AddPlanEntry(string repoId, string remoteName, string oldUrl, string newUrl, bool isUpdate)
    {
        _logger.LogDebug("  Plan: {repoId} remote '{name}' {op} → {url}",
            repoId, remoteName, isUpdate ? "set-url" : "add", newUrl);

        _state.State.RemoteUpdates.Add(new RemoteUpdateState
        {
            RepoId = repoId,
            RemoteName = remoteName,
            OldUrl = oldUrl,
            NewUrl = newUrl,
            IsUpdate = isUpdate,
            IsDone = false
        });
    }

    // =========================================================================
    // Utility helpers
    // =========================================================================

    /// <summary>Builds a map of normalised URL → local filesystem path for all successfully cloned repos.</summary>
    private Dictionary<string, string> BuildUrlMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in _state.State.DiscoveredRepos)
        {
            if (!_state.State.CloneStates.TryGetValue(repo.Id, out var cs) ||
                cs.Status != CloneStatus.Complete)
                continue;

            void TryAdd(string? url)
            {
                if (string.IsNullOrEmpty(url)) return;
                var norm = UrlHelper.Normalize(url);
                if (!string.IsNullOrEmpty(norm))
                    map.TryAdd(norm, repo.LocalPath);
            }

            TryAdd(repo.CloneUrl);
            TryAdd(repo.SshUrl);
        }
        return map;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a.TrimEnd(Path.DirectorySeparatorChar)),
            Path.GetFullPath(b.TrimEnd(Path.DirectorySeparatorChar)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns (cloneUrl, cleanUrl?) where cloneUrl may have embedded credentials
    /// and cleanUrl (if non-null) is the credential-free URL to store in .git/config.
    /// </summary>
    private (string cloneUrl, string? cleanUrl) BuildCloneUrls(RepoInfo repo)
    {
        var source = _cfg.Sources.FirstOrDefault(s => s.Type == repo.SourceType);

        // LAN repos use SSH key auth – no credentials needed
        if (repo.SourceType == "lan" || source is null)
            return (repo.CloneUrl, null);

        // Prefer SSH if configured and an SSH URL is available
        if (_cfg.PreferSsh && repo.SshUrl is { Length: > 0 })
            return (repo.SshUrl, null);

        // HTTPS with embedded token (token is stripped from .git/config after clone)
        string authUrl = source switch
        {
            GitHubSourceConfig gh when !string.IsNullOrEmpty(gh.Token) =>
                UrlHelper.WithGitHubToken(repo.CloneUrl, gh.Token),

            GitLabSourceConfig gl when !string.IsNullOrEmpty(gl.Token) =>
                UrlHelper.WithGitLabToken(repo.CloneUrl, gl.Token),

            _ => repo.CloneUrl
        };

        string cleanUrl = UrlHelper.StripCredentials(authUrl);
        return (authUrl, authUrl != cleanUrl ? cleanUrl : null);
    }
}
