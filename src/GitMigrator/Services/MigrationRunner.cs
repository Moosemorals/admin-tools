using GitMigrator.Models;
using Microsoft.Extensions.Logging;

namespace GitMigrator.Services;

/// <summary>
/// Orchestrates the four-phase migration:
///   1. Discovery       – query all sources.
///   2. Cloning         – clone each discovered repo using a flat basename layout.
///   3. Grouping        – use shared git commit SHAs (Union-Find) to identify repos
///                        that are copies of the same repository across sources, then
///                        merge them into a single canonical local clone.
///   4. Remote wiring   – update remotes of local clones so they reference each other
///                        by local path wherever possible.
///
/// The state file is written after each individual operation so the process can be
/// interrupted and resumed without repeating completed work.
/// </summary>
public class MigrationRunner
{
    private const string MergedRemotePrefix = "merged-";

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

            _state.State.Phase = MigrationPhase.Grouping;
            _state.Save();
        }

        // -----------------------------------------------------------------------
        // Phase 3: Grouping – merge repos sharing git history into one folder
        // -----------------------------------------------------------------------
        if (_state.State.Phase == MigrationPhase.Grouping)
        {
            _logger.LogInformation("═══ Phase 3: Grouping by shared git history ═══");
            await RunGroupingAsync();

            _state.State.Phase = MigrationPhase.UpdatingRemotes;
            _state.Save();
        }

        // -----------------------------------------------------------------------
        // Phase 4: Remote wiring
        // -----------------------------------------------------------------------
        if (_state.State.Phase == MigrationPhase.UpdatingRemotes)
        {
            _logger.LogInformation("═══ Phase 4: Updating remotes ═══");
            await RunRemoteReconciliationAsync();

            _state.State.Phase = MigrationPhase.Complete;
            _state.Save();
        }

        if (_state.State.Phase == MigrationPhase.Complete)
        {
            var cloned  = _state.State.CloneStates.Values.Count(c => c.Status == CloneStatus.Complete);
            var failed  = _state.State.CloneStates.Values.Count(c => c.Status == CloneStatus.Failed);
            var groups  = _state.State.RepoGroups.Count;
            var merged  = _state.State.RepoGroups.Sum(g => g.MemberRepoIds.Count - 1);
            _logger.LogInformation(
                "Migration complete. Cloned: {ok}, Failed: {fail}, Groups: {g}, Merged: {m}.",
                cloned, failed, groups, merged);
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
                        _loggerFactory.CreateLogger<GitHubDiscovery>()).DiscoverReposAsync(),

                    GitLabSourceConfig glCfg => await new GitLabDiscovery(
                        glCfg,
                        new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                        _loggerFactory.CreateLogger<GitLabDiscovery>()).DiscoverReposAsync(),

                    LanSourceConfig lanCfg => await new LanDiscovery(
                        lanCfg,
                        _loggerFactory.CreateLogger<LanDiscovery>()).DiscoverReposAsync(),

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
                if (_state.State.DiscoveredRepos.Any(r => r.Id == repo.Id))
                {
                    _logger.LogDebug("Skipping duplicate repo id: {id}", repo.Id);
                    continue;
                }
                _state.State.DiscoveredRepos.Add(repo);
            }
        }

        _logger.LogInformation("Discovery complete. Total repos: {count}.", _state.State.DiscoveredRepos.Count);

        // Assign flat local paths now that all repos are known.
        AssignLocalPaths();

        _state.Save();
    }

    // =========================================================================
    // Path assignment helpers
    // =========================================================================

    /// <summary>
    /// Assigns a flat local path (<c>targetFolder/basename</c>) to every discovered repo.
    /// Repos are ordered by source priority (GitHub &gt; GitLab &gt; LAN) so that the "best"
    /// source gets the undecorated name when there are collisions.
    /// Repos whose names collide with an already-assigned slot get a numeric suffix
    /// (<c>myapp-2</c>, <c>myapp-3</c>, …).  The Grouping phase may later consolidate
    /// multiple repos into one folder when they share git history.
    /// </summary>
    private void AssignLocalPaths()
    {
        // Sort by source priority (LAN > GitLab > GitHub), then alphabetically for determinism
        var ordered = _state.State.DiscoveredRepos
            .OrderBy(r => r.SourceType switch { "lan" => 0, "gitlab" => 1, _ => 2 })
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var repo in ordered)
        {
            var baseName = SanitizeFolderName(repo.Name);
            if (string.IsNullOrEmpty(baseName))
                baseName = SanitizeFolderName(repo.Id.Replace(":", "-").Replace("/", "-"));

            if (!usedNames.TryGetValue(baseName, out var count))
            {
                usedNames[baseName] = 1;
                repo.LocalPath = Path.Combine(_cfg.TargetFolder, baseName);
            }
            else
            {
                usedNames[baseName] = count + 1;
                repo.LocalPath = Path.Combine(_cfg.TargetFolder, $"{baseName}-{count + 1}");
            }

            _logger.LogDebug("Assigned {id} → {path}", repo.Id, repo.LocalPath);
        }
    }

    private static string SanitizeFolderName(string name)
    {
        // Include '/' explicitly: on Windows it is not in GetInvalidFileNameChars()
        // but we never want a slash in a directory basename regardless of platform.
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/' };
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '-' : c);
        return sb.ToString().Trim('-', '.');
    }

    // =========================================================================
    // Phase 2: Cloning
    // =========================================================================

    private async Task RunCloningAsync()
    {
        var repos  = _state.State.DiscoveredRepos;
        int total  = repos.Count;
        int idx    = 0;

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

            bool   success  = false;
            string? errorMsg = null;

            try
            {
                success = await _git.CloneAsync(cloneUrl, repo.LocalPath, cleanUrl, timeoutSeconds: 900);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error cloning {name}.", repo.FullName);
                errorMsg = ex.Message;
            }

            _state.State.CloneStates[repo.Id] = new RepoCloneState
            {
                RepoId      = repo.Id,
                LocalPath   = repo.LocalPath,  // original cloned path (never updated after this)
                Status      = success ? CloneStatus.Complete : CloneStatus.Failed,
                Error       = success ? null : (errorMsg ?? "Clone failed; see log for details."),
                CompletedAt = success ? DateTime.UtcNow : null
            };

            _state.Save();
        }

        int ok   = _state.State.CloneStates.Values.Count(c => c.Status == CloneStatus.Complete);
        int fail = _state.State.CloneStates.Values.Count(c => c.Status == CloneStatus.Failed);
        _logger.LogInformation("Cloning complete. Succeeded: {ok}, Failed: {fail}.", ok, fail);

        if (fail > 0)
            _logger.LogWarning("{fail} repo(s) failed. Re-run to retry them automatically.", fail);
    }

    // =========================================================================
    // Phase 3: Grouping
    // =========================================================================

    private async Task RunGroupingAsync()
    {
        var cloned = _state.State.DiscoveredRepos
            .Where(r => _state.State.CloneStates.TryGetValue(r.Id, out var cs)
                        && cs.Status == CloneStatus.Complete)
            .ToList();

        if (cloned.Count <= 1)
        {
            _logger.LogInformation("Only {n} cloned repo(s) – nothing to group.", cloned.Count);
            // Still create trivial groups so Phase 4 has a consistent structure to iterate.
            if (_state.State.RepoGroups.Count == 0)
            {
                foreach (var r in cloned)
                    _state.State.RepoGroups.Add(new RepoGroup
                    {
                        CanonicalRepoId  = r.Id,
                        MemberRepoIds    = [r.Id],
                        FetchComplete    = true
                    });
                _state.Save();
            }
            _state.State.GroupingComplete = true;
            return;
        }

        // ------------------------------------------------------------------
        // Build groups (only if not already persisted from a previous run)
        // ------------------------------------------------------------------
        if (_state.State.RepoGroups.Count == 0)
        {
            _logger.LogInformation(
                "Collecting all commit SHAs from {n} cloned repos (this may take a while)...",
                cloned.Count);

            // Collect all commit SHAs reachable from any ref in each repo.
            // This is O(total-commits) and is the most accurate way to detect
            // whether repos share history (including forks and cherry-picks).
            var repoShas = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var repo in cloned)
            {
                var shas = await _git.GetAllCommitShasAsync(repo.LocalPath);
                repoShas[repo.Id] = shas;
                _logger.LogDebug("{id}: {count} commit(s)", repo.Id, shas.Count);
            }

            // Union-Find: repos that share any commit SHA are placed in the same group.
            // Repo IDs are stable opaque strings – use Ordinal comparison for speed.
            var parent = cloned.ToDictionary(r => r.Id, r => r.Id, StringComparer.Ordinal);

            string Find(string x)
            {
                if (parent[x] != x) parent[x] = Find(parent[x]);
                return parent[x];
            }

            void Union(string x, string y)
            {
                var px = Find(x);
                var py = Find(y);
                if (px != py) parent[px] = py;
            }

            // Map SHA → first repo that has it; union when we find a second.
            var shaToRepo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (repoId, shas) in repoShas)
            {
                foreach (var sha in shas)
                {
                    if (shaToRepo.TryGetValue(sha, out var other))
                        Union(repoId, other);
                    else
                        shaToRepo[sha] = repoId;
                }
            }

            // Build group membership lists.
            var membersByRoot = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var repo in cloned)
            {
                var root = Find(repo.Id);
                if (!membersByRoot.TryGetValue(root, out var members))
                    membersByRoot[root] = members = [];
                members.Add(repo.Id);
            }

            int multiGroup = membersByRoot.Values.Count(m => m.Count > 1);
            _logger.LogInformation(
                "Grouping complete: {groups} group(s), {multi} multi-repo group(s).",
                membersByRoot.Count, multiGroup);

            foreach (var (_, memberIds) in membersByRoot)
            {
                // Within a group the canonical clone is the one from the most authoritative source
                // (LAN wins over GitLab wins over GitHub), with ties broken by full name.
                var sorted = memberIds
                    .Select(id => _state.State.DiscoveredRepos.First(r => r.Id == id))
                    .OrderBy(r => r.SourceType switch { "lan" => 0, "gitlab" => 1, _ => 2 })
                    .ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _state.State.RepoGroups.Add(new RepoGroup
                {
                    CanonicalRepoId  = sorted[0].Id,
                    MemberRepoIds    = sorted.Select(r => r.Id).ToList(),
                    FetchComplete    = memberIds.Count == 1  // trivial groups need no fetching
                });
            }

            _state.Save();
        }

        // ------------------------------------------------------------------
        // Consolidate: fetch non-canonical members into canonical, then remove
        // the redundant clone directories.
        // ------------------------------------------------------------------
        foreach (var group in _state.State.RepoGroups)
        {
            if (group.FetchComplete) continue;

            var canonical = _state.State.DiscoveredRepos.First(r => r.Id == group.CanonicalRepoId);

            _logger.LogInformation(
                "Consolidating group (canonical: {canon}):", canonical.FullName);

            int remoteIdx = 0;
            foreach (var memberId in group.MemberRepoIds)
            {
                if (memberId == group.CanonicalRepoId) continue;
                if (group.ConsolidatedMemberIds.Contains(memberId)) continue;

                var member = _state.State.DiscoveredRepos.First(r => r.Id == memberId);

                // The original cloned directory for this member (never changes in CloneState)
                var memberOriginalPath = _state.State.CloneStates.TryGetValue(memberId, out var cs)
                    ? cs.LocalPath
                    : member.LocalPath;

                _logger.LogInformation("  Merging {member} into {canon}...",
                    member.FullName, canonical.FullName);

                // Remote name: "<prefix><sanitized-member-name>[-N]"
                // Using the member's name makes the remote self-documenting in `git remote -v`.
                var baseRemoteName = MergedRemotePrefix + SanitizeFolderName(member.Name);
                var remoteName = baseRemoteName;
                // Append index only when needed to avoid collisions within this canonical
                if (remoteIdx > 0) remoteName = $"{baseRemoteName}-{remoteIdx}";
                remoteIdx++;

                // Add the member's original path as a temporary remote in canonical, fetch all refs.
                var existingRemotes = await _git.GetRemotesAsync(canonical.LocalPath);
                if (!existingRemotes.ContainsKey(remoteName))
                    await _git.AddRemoteAsync(canonical.LocalPath, remoteName, memberOriginalPath);

                bool fetchOk = await _git.FetchRemoteAsync(canonical.LocalPath, remoteName);

                if (fetchOk)
                {
                    // Re-point the "merged-*" remote to the member's original clone URL so that
                    // `git fetch merged-N` continues to work (and the local dir can be deleted).
                    await _git.SetRemoteUrlAsync(canonical.LocalPath, remoteName, member.CloneUrl);

                    // Update the member's effective path to canonical's path.
                    member.LocalPath = canonical.LocalPath;

                    // Remove the now-redundant local clone directory.
                    if (!_cfg.DryRun
                        && Directory.Exists(memberOriginalPath)
                        && !PathsEqual(memberOriginalPath, canonical.LocalPath))
                    {
                        try
                        {
                            Directory.Delete(memberOriginalPath, recursive: true);
                            _logger.LogInformation("  Removed redundant clone: {path}", memberOriginalPath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "  Could not remove redundant clone: {path}", memberOriginalPath);
                        }
                    }

                    group.ConsolidatedMemberIds.Add(memberId);
                    _state.Save();
                }
                else
                {
                    _logger.LogWarning(
                        "  Failed to fetch {member} into canonical – keeping separate clone.",
                        member.FullName);
                }
            }

            group.FetchComplete = true;
            _state.Save();
        }

        _state.State.GroupingComplete = true;
        _state.Save();
    }

    // =========================================================================
    // Phase 4: Remote reconciliation
    // =========================================================================

    private async Task RunRemoteReconciliationAsync()
    {
        // Build URL → canonical local path map from all successfully cloned repos.
        // After grouping, all non-canonical repos' LocalPath equals the canonical's path,
        // so every group member's URLs map to the canonical directory.
        var urlToLocal = BuildUrlMap();

        if (urlToLocal.Count == 0)
        {
            _logger.LogInformation("No cloned repos to reconcile remotes for.");
            return;
        }

        // Plan remote updates (idempotent: skip if already planned from a previous run)
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

        // Build repoId → localPath lookup for the execution step.
        var idToPath = _state.State.DiscoveredRepos
            .ToDictionary(r => r.Id, r => r.LocalPath, StringComparer.OrdinalIgnoreCase);

        int done = 0, skipped = 0, failed = 0;
        foreach (var update in _state.State.RemoteUpdates)
        {
            if (update.IsDone) { skipped++; continue; }

            if (!idToPath.TryGetValue(update.RepoId, out var localPath))
            {
                _logger.LogWarning("Cannot find local path for repo '{id}', skipping.", update.RepoId);
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
                _logger.LogError(ex, "Remote update failed for {repo}/{remote}.",
                    update.RepoId, update.RemoteName);
                ok = false;
                update.Error = ex.Message;
            }

            update.IsDone = ok;
            if (ok) done++; else failed++;
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

        // Iterate over canonical clones only (one per group).
        foreach (var group in _state.State.RepoGroups)
        {
            var canonical = _state.State.DiscoveredRepos.First(r => r.Id == group.CanonicalRepoId);

            if (!_state.State.CloneStates.TryGetValue(group.CanonicalRepoId, out var cs)
                || cs.Status != CloneStatus.Complete)
                continue;

            var localPath      = canonical.LocalPath;
            var currentRemotes = await _git.GetRemotesAsync(localPath);

            // ── A. Update existing remotes that now have local equivalents ────────
            // If any remote URL resolves to another canonical clone's local path,
            // update it so git operations work fully offline.
            foreach (var (remoteName, remoteUrl) in currentRemotes)
            {
                // Skip remotes already added by the grouping phase (merged-*)
                if (remoteName.StartsWith(MergedRemotePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var norm = UrlHelper.Normalize(remoteUrl);
                if (urlToLocal.TryGetValue(norm, out var localTarget)
                    && !PathsEqual(localTarget, localPath))
                {
                    AddPlanEntry(group.CanonicalRepoId, remoteName, remoteUrl, localTarget, isUpdate: true);
                }
            }

            // ── B. Original LAN remotes for all group members ─────────────────────
            // The source LAN repo may have had its own remotes (e.g. origin → GitHub).
            // Surface those as 'local-<name>' remotes in the canonical clone so the
            // user can fetch from sibling repos directly.
            foreach (var memberId in group.MemberRepoIds)
            {
                var memberRepo = _state.State.DiscoveredRepos.First(r => r.Id == memberId);
                foreach (var (remoteName, remoteUrl) in memberRepo.OriginalRemotes)
                {
                    var norm = UrlHelper.Normalize(remoteUrl);
                    if (!urlToLocal.TryGetValue(norm, out var localTarget)) continue;
                    if (PathsEqual(localTarget, localPath)) continue;

                    var newRemoteName = $"local-{remoteName}";
                    bool alreadyPlanned = _state.State.RemoteUpdates.Any(
                        u => u.RepoId == group.CanonicalRepoId && u.RemoteName == newRemoteName);
                    bool alreadyExists = currentRemotes.ContainsKey(newRemoteName);
                    if (!alreadyPlanned && !alreadyExists)
                        AddPlanEntry(group.CanonicalRepoId, newRemoteName, remoteUrl, localTarget, isUpdate: false);
                }
            }

            // ── C. Fork parents ───────────────────────────────────────────────────
            // If any group member is a fork, add an 'upstream' remote pointing to the
            // canonical clone of the parent (if we have it locally).
            foreach (var memberId in group.MemberRepoIds)
            {
                var memberRepo = _state.State.DiscoveredRepos.First(r => r.Id == memberId);
                if (memberRepo.ForkOf is not { Length: > 0 } parentUrl) continue;

                var norm = UrlHelper.Normalize(parentUrl);
                if (!urlToLocal.TryGetValue(norm, out var parentLocal)) continue;
                if (PathsEqual(parentLocal, localPath)) continue;

                const string upstreamName = "upstream";
                bool alreadyPlanned = _state.State.RemoteUpdates.Any(
                    u => u.RepoId == group.CanonicalRepoId && u.RemoteName == upstreamName);
                bool alreadyExists = currentRemotes.ContainsKey(upstreamName);
                if (!alreadyPlanned && !alreadyExists)
                    AddPlanEntry(group.CanonicalRepoId, upstreamName, parentUrl, parentLocal, isUpdate: false);
            }
        }

        _logger.LogInformation("Planned {count} remote operation(s).", _state.State.RemoteUpdates.Count);
    }

    private void AddPlanEntry(string repoId, string remoteName, string oldUrl, string newUrl, bool isUpdate)
    {
        _logger.LogDebug("  Plan: {id} remote '{name}' {op} → {url}",
            repoId, remoteName, isUpdate ? "set-url" : "add", newUrl);

        _state.State.RemoteUpdates.Add(new RemoteUpdateState
        {
            RepoId     = repoId,
            RemoteName = remoteName,
            OldUrl     = oldUrl,
            NewUrl     = newUrl,
            IsUpdate   = isUpdate,
            IsDone     = false
        });
    }

    // =========================================================================
    // Utility helpers
    // =========================================================================

    /// <summary>
    /// Builds a map: normalised URL → canonical local filesystem path.
    /// After grouping, all group members have LocalPath = canonical.LocalPath,
    /// so this map naturally routes any member URL to the single canonical clone.
    /// </summary>
    private Dictionary<string, string> BuildUrlMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in _state.State.DiscoveredRepos)
        {
            if (!_state.State.CloneStates.TryGetValue(repo.Id, out var cs)
                || cs.Status != CloneStatus.Complete)
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
    /// Returns (cloneUrl, cleanUrl?) where cloneUrl may embed credentials
    /// and cleanUrl is the credential-free URL stored in .git/config after clone.
    /// </summary>
    private (string cloneUrl, string? cleanUrl) BuildCloneUrls(RepoInfo repo)
    {
        var source = _cfg.Sources.FirstOrDefault(s => s.Type == repo.SourceType);

        if (repo.SourceType == "lan" || source is null)
            return (repo.CloneUrl, null);

        if (_cfg.PreferSsh && repo.SshUrl is { Length: > 0 })
            return (repo.SshUrl, null);

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
