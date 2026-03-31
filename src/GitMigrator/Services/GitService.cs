using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GitMigrator.Services;

/// <summary>
/// Wraps the git command-line tool. All operations are asynchronous and non-interactive
/// (GIT_TERMINAL_PROMPT=0 prevents git from hanging waiting for credentials).
/// </summary>
public class GitService
{
    private readonly ILogger<GitService> _logger;
    private readonly bool _dryRun;

    public GitService(ILogger<GitService> logger, bool dryRun = false)
    {
        _logger = logger;
        _dryRun = dryRun;
    }

    // -------------------------------------------------------------------------
    // Core execution helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs a git command and returns (success, stdout, stderr).
    /// The caller is responsible for sanitising <paramref name="args"/> before logging.
    /// </summary>
    public async Task<(bool Success, string Output, string Error)> RunAsync(
        string workDir,
        IEnumerable<string> args,
        Dictionary<string, string>? extraEnv = null,
        int timeoutSeconds = 600)
    {
        var argList = args.ToList();
        // Mask any arg that looks like it contains a token (has '@' after "https://...")
        var displayArgs = argList.Select(MaskSecrets);
        _logger.LogDebug("git {args} (cwd: {dir})", string.Join(' ', displayArgs), workDir);

        if (_dryRun)
        {
            _logger.LogInformation("[DRY RUN] git {args}", string.Join(' ', displayArgs));
            return (true, string.Empty, string.Empty);
        }

        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in argList)
            psi.ArgumentList.Add(arg);

        // Prevent interactive credential prompts that would stall the process
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_ASKPASS"] = "true"; // 'true' exits 0 with no output → empty password

        if (extraEnv is not null)
            foreach (var (k, v) in extraEnv)
                psi.Environment[k] = v;

        using var process = new Process { StartInfo = psi };
        process.Start();

        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();

        var exited = await Task.Run(() => process.WaitForExit(timeoutSeconds * 1000));
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            _logger.LogError("git {args} timed out after {sec}s", string.Join(' ', displayArgs), timeoutSeconds);
            return (false, string.Empty, "Process timed out.");
        }

        var stdout = await outTask;
        var stderr = await errTask;
        var success = process.ExitCode == 0;

        if (!success)
            _logger.LogDebug("git exit {code}: {err}", process.ExitCode, stderr.Trim());

        return (success, stdout, stderr);
    }

    // -------------------------------------------------------------------------
    // High-level operations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Clone <paramref name="cloneUrl"/> into <paramref name="localPath"/>.
    /// If the target directory already exists, the clone is skipped (idempotent).
    /// Credentials embedded in the URL are stripped from the remote config after cloning.
    /// </summary>
    public async Task<bool> CloneAsync(
        string cloneUrl,
        string localPath,
        string? cleanUrl = null,
        int timeoutSeconds = 900)
    {
        if (Directory.Exists(Path.Combine(localPath, ".git")) ||
            Directory.Exists(Path.Combine(localPath, "objects"))) // bare repo
        {
            _logger.LogInformation("Already cloned, skipping: {path}", localPath);
            return true;
        }

        if (Directory.Exists(localPath) && Directory.EnumerateFileSystemEntries(localPath).Any())
        {
            _logger.LogWarning("Target directory exists and is non-empty but is not a git repo: {path}", localPath);
            return false;
        }

        var parentDir = Path.GetDirectoryName(localPath) ?? ".";
        if (!_dryRun)
            Directory.CreateDirectory(parentDir);

        _logger.LogInformation("Cloning {url} → {path}",
            cleanUrl is not null ? cleanUrl : UrlHelper.StripCredentials(cloneUrl),
            localPath);

        var args = new List<string> { "clone", "--progress", cloneUrl, localPath };
        var (success, _, error) = await RunAsync(parentDir, args, timeoutSeconds: timeoutSeconds);

        if (!success)
        {
            _logger.LogError("Clone failed for {url}: {err}",
                cleanUrl is not null ? cleanUrl : UrlHelper.StripCredentials(cloneUrl),
                error.Trim());
            return false;
        }

        // If an authenticated URL was used, replace the stored remote URL with the clean version
        // so the access token is not persisted in .git/config.
        if (cleanUrl is not null && cleanUrl != cloneUrl && !_dryRun)
        {
            await SetRemoteUrlAsync(localPath, "origin", cleanUrl);
        }

        return true;
    }

    /// <summary>Returns all fetch remotes as {name → url}.</summary>
    public async Task<Dictionary<string, string>> GetRemotesAsync(string localPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(localPath))
            return result;

        var (success, output, _) = await RunAsync(localPath, ["remote", "-v"]);
        if (!success)
            return result;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Format: "name\turl (fetch)" or "name\turl (push)"
            var tabIdx = line.IndexOf('\t');
            if (tabIdx < 0)
                continue;

            var name = line[..tabIdx].Trim();
            var rest = line[(tabIdx + 1)..].Trim();

            // Keep only fetch entries
            if (!rest.EndsWith("(fetch)", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = rest[..^7].Trim(); // strip " (fetch)"
            result[name] = url;
        }

        return result;
    }

    public async Task<bool> RemoteExistsAsync(string localPath, string remoteName)
    {
        var remotes = await GetRemotesAsync(localPath);
        return remotes.ContainsKey(remoteName);
    }

    public async Task<bool> SetRemoteUrlAsync(string localPath, string remoteName, string newUrl)
    {
        _logger.LogInformation("  remote set-url {name} → {url} (in {path})", remoteName, newUrl, localPath);
        var (success, _, error) = await RunAsync(localPath, ["remote", "set-url", remoteName, newUrl]);
        if (!success)
            _logger.LogError("  remote set-url failed: {err}", error.Trim());
        return success;
    }

    public async Task<bool> AddRemoteAsync(string localPath, string remoteName, string url)
    {
        _logger.LogInformation("  remote add {name} {url} (in {path})", remoteName, url, localPath);
        var (success, _, error) = await RunAsync(localPath, ["remote", "add", remoteName, url]);
        if (!success)
            _logger.LogError("  remote add failed: {err}", error.Trim());
        return success;
    }

    /// <summary>Returns true when <paramref name="path"/> is the root of a git working tree or bare repo.</summary>
    public async Task<bool> IsGitRepoAsync(string path)
    {
        if (!Directory.Exists(path))
            return false;

        var (success, _, _) = await RunAsync(path, ["rev-parse", "--git-dir"], timeoutSeconds: 10);
        return success;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static string MaskSecrets(string arg)
    {
        // Mask embedded credentials in URLs like https://TOKEN@host/...
        // or https://oauth2:TOKEN@host/...
        if (!arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return arg;

        var afterScheme = arg[8..];
        var atIdx = afterScheme.IndexOf('@');
        var firstSlash = afterScheme.IndexOf('/');
        if (atIdx >= 0 && (firstSlash < 0 || atIdx < firstSlash))
            return "https://[REDACTED]@" + afterScheme[(atIdx + 1)..];

        return arg;
    }
}
