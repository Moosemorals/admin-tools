using System.Diagnostics;
using GitMigrator.Models;
using Microsoft.Extensions.Logging;

namespace GitMigrator.Services;

/// <summary>
/// Discovers and prepares repository information from LAN machines accessed via SSH.
/// Uses passwordless SSH (key-based authentication).
/// </summary>
public class LanDiscovery
{
    private readonly LanSourceConfig _cfg;
    private readonly ILogger<LanDiscovery> _logger;
    private readonly string _targetFolder;

    public LanDiscovery(
        LanSourceConfig cfg,
        ILogger<LanDiscovery> logger,
        string targetFolder)
    {
        _cfg = cfg;
        _logger = logger;
        _targetFolder = targetFolder;
    }

    public async Task<List<RepoInfo>> DiscoverReposAsync()
    {
        var repos = new List<RepoInfo>();

        // Collect explicit repo paths
        foreach (var repoPath in _cfg.Repos)
        {
            var info = await BuildRepoInfoAsync(repoPath.TrimEnd('/'));
            if (info is not null)
                repos.Add(info);
        }

        // Auto-discover in scan paths
        foreach (var scanPath in _cfg.ScanPaths)
        {
            _logger.LogInformation("LAN({host}): scanning '{path}' for git repos...", _cfg.Host, scanPath);
            var found = await FindReposViaSshAsync(scanPath.TrimEnd('/'));
            foreach (var repoPath in found)
            {
                var info = await BuildRepoInfoAsync(repoPath);
                if (info is not null && repos.All(r => r.CloneUrl != info.CloneUrl))
                    repos.Add(info);
            }
        }

        _logger.LogInformation("LAN({host}): found {count} repo(s).", _cfg.Host, repos.Count);
        return repos;
    }

    // -------------------------------------------------------------------------
    // SSH discovery helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs a remote find command to locate git repositories under <paramref name="rootPath"/>.
    /// Detects both bare repos and regular (working-tree) repos.
    /// </summary>
    private async Task<List<string>> FindReposViaSshAsync(string rootPath)
    {
        // Strategy:
        //   1. Find all ".git" directories → their parent is a working-tree repo.
        //   2. Find HEAD files NOT inside a .git dir → likely a bare repo root.
        // We combine both, deduplicate, and verify each with 'git rev-parse --git-dir'.
        var depth = _cfg.MaxScanDepth;

        // Use $$""" so that {{ and }} are literal braces and {{rootPath}}/{{depth}} are the interpolations.
        var findScript = $$"""
            set -o pipefail 2>/dev/null || true
            (
              find '{{rootPath}}' -maxdepth {{depth}} -name '.git' -type d 2>/dev/null \
                | sed 's|/.git$||'
              find '{{rootPath}}' -maxdepth {{depth}} -name 'HEAD' -type f \
                -not -path '*/.git/*' 2>/dev/null \
                | xargs -I{} dirname {}
            ) | sort -u | while IFS= read -r d; do
              git -C "$d" rev-parse --git-dir > /dev/null 2>&1 && echo "$d"
            done
            """;

        var (success, output, error) = await RunSshAsync(findScript, timeoutSeconds: 120);
        if (!success)
        {
            _logger.LogWarning("LAN({host}): scan of '{path}' failed or returned errors: {err}",
                _cfg.Host, rootPath, error.Trim());
        }

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)
            .ToList();
    }

    /// <summary>Fetches remote configuration for a repo and builds its <see cref="RepoInfo"/>.</summary>
    private async Task<RepoInfo?> BuildRepoInfoAsync(string remotePath)
    {
        _logger.LogDebug("LAN({host}): inspecting '{path}'...", _cfg.Host, remotePath);

        // Verify it's a git repo and get the name
        var (ok, nameOut, _) = await RunSshAsync(
            $"git -C '{remotePath}' rev-parse --show-toplevel 2>/dev/null || git -C '{remotePath}' rev-parse --absolute-git-dir 2>/dev/null | sed 's|/.git$||'",
            timeoutSeconds: 15);

        if (!ok || string.IsNullOrWhiteSpace(nameOut))
        {
            _logger.LogWarning("LAN({host}): '{path}' does not appear to be a git repo, skipping.", _cfg.Host, remotePath);
            return null;
        }

        var topLevel = nameOut.Trim().Split('\n')[0].Trim();
        var repoName = Path.GetFileName(topLevel.TrimEnd('/')).TrimEnd('/');
        if (repoName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repoName = repoName[..^4];

        // Collect the repo's own remotes (for later reconciliation)
        var originalRemotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var (_, remoteOut, _) = await RunSshAsync(
            $"git -C '{remotePath}' remote -v 2>/dev/null", timeoutSeconds: 15);
        foreach (var line in remoteOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var rName = line[..tab].Trim();
            var rest = line[(tab + 1)..].Trim();
            if (!rest.EndsWith("(fetch)", StringComparison.OrdinalIgnoreCase)) continue;
            var rUrl = rest[..^7].Trim();
            // If the remote URL is a local path on the same LAN machine (starts with /),
            // normalise it to an SSH URL so it can be matched against the URL map.
            if (rUrl.StartsWith('/') || rUrl.StartsWith("~/"))
                rUrl = _cfg.SshPort != 22
                    ? $"ssh://{_cfg.SshTarget}:{_cfg.SshPort}{rUrl}"
                    : $"{_cfg.SshTarget}:{rUrl}";
            originalRemotes[rName] = rUrl;
        }

        // Get default branch
        var (_, branchOut, _) = await RunSshAsync(
            $"git -C '{remotePath}' symbolic-ref --short HEAD 2>/dev/null || echo main",
            timeoutSeconds: 10);
        var defaultBranch = branchOut.Trim().Split('\n')[0].Trim();
        if (string.IsNullOrEmpty(defaultBranch)) defaultBranch = "main";

        // Build SSH clone URL
        var sshTarget = _cfg.SshTarget;
        string sshCloneUrl = _cfg.SshPort != 22
            ? $"ssh://{sshTarget}:{_cfg.SshPort}{remotePath}"
            : $"{sshTarget}:{remotePath}";

        // Local path: {targetFolder}/lan/{hostname}/{absolute-path-on-host}
        // Strip leading slash from remotePath so Path.Combine works correctly
        var relPath = remotePath.TrimStart('/');
        var localPath = Path.Combine(_targetFolder, "lan", _cfg.Host, relPath);

        var id = $"lan:{_cfg.Host}:{remotePath}";

        return new RepoInfo
        {
            Id = id,
            SourceType = "lan",
            SourceHost = _cfg.Host,
            Name = repoName,
            FullName = $"{_cfg.Host}{remotePath}",
            CloneUrl = sshCloneUrl,   // for LAN, CloneUrl and SshUrl are the same
            SshUrl = sshCloneUrl,
            LocalPath = localPath,
            DefaultBranch = defaultBranch,
            OriginalRemotes = originalRemotes
        };
    }

    // -------------------------------------------------------------------------
    // SSH execution helper
    // -------------------------------------------------------------------------

    private async Task<(bool Success, string Output, string Error)> RunSshAsync(
        string remoteCommand,
        int timeoutSeconds = 60)
    {
        var args = new List<string>
        {
            "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", $"ConnectTimeout={Math.Min(timeoutSeconds, 30)}"
        };

        if (_cfg.SshPort != 22)
        {
            args.Add("-p");
            args.Add(_cfg.SshPort.ToString());
        }

        args.Add(_cfg.SshTarget);
        args.Add(remoteCommand);

        _logger.LogDebug("SSH {target}: {cmd}", _cfg.SshTarget, remoteCommand.Split('\n')[0].Trim());

        var psi = new ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();

        var exited = await Task.Run(() => process.WaitForExit(timeoutSeconds * 1000));
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (false, string.Empty, "SSH command timed out.");
        }

        return (process.ExitCode == 0, await outTask, await errTask);
    }
}
