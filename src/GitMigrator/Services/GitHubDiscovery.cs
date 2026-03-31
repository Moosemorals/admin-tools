using System.Net.Http.Headers;
using System.Text.Json;
using GitMigrator.Models;
using Microsoft.Extensions.Logging;

namespace GitMigrator.Services;

/// <summary>
/// Discovers repositories from the GitHub REST API (v3).
/// Handles pagination and maps GitHub API responses to <see cref="RepoInfo"/>.
/// </summary>
public class GitHubDiscovery
{
    private readonly GitHubSourceConfig _cfg;
    private readonly HttpClient _http;
    private readonly ILogger<GitHubDiscovery> _logger;

    public GitHubDiscovery(
        GitHubSourceConfig cfg,
        HttpClient http,
        ILogger<GitHubDiscovery> logger)
    {
        _cfg = cfg;
        _http = http;
        _logger = logger;

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", cfg.Token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GitMigrator/1.0");
        // GitHub requires the Accept header for the REST API
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<List<RepoInfo>> DiscoverReposAsync()
    {
        var repos = new List<RepoInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Determine which users to query
        string? authenticatedUser = null;
        try
        {
            var userJson = await _http.GetStringAsync($"{_cfg.BaseUrl}/user");
            using var userDoc = JsonDocument.Parse(userJson);
            authenticatedUser = userDoc.RootElement.GetProperty("login").GetString();
            _logger.LogInformation("GitHub: authenticated as '{user}'.", authenticatedUser);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub: could not retrieve authenticated user info.");
        }

        var users = _cfg.Users.Count > 0 ? _cfg.Users
                    : authenticatedUser is not null ? new List<string> { authenticatedUser }
                    : new List<string>();

        foreach (var user in users)
        {
            _logger.LogInformation("GitHub: discovering repos for user '{user}'...", user);
            bool isAuthUser = string.Equals(user, authenticatedUser, StringComparison.OrdinalIgnoreCase);
            await foreach (var repo in GetUserReposAsync(user, isAuthUser))
            {
                if (seen.Add(repo.CloneUrl))
                    repos.Add(repo);
            }
        }

        foreach (var org in _cfg.Orgs)
        {
            _logger.LogInformation("GitHub: discovering repos for org '{org}'...", org);
            await foreach (var repo in GetOrgReposAsync(org))
            {
                if (seen.Add(repo.CloneUrl))
                    repos.Add(repo);
            }
        }

        _logger.LogInformation("GitHub: found {count} repo(s).", repos.Count);
        return repos;
    }

    // -------------------------------------------------------------------------
    // Pagination helpers
    // -------------------------------------------------------------------------

    private async IAsyncEnumerable<RepoInfo> GetUserReposAsync(string username, bool isAuthenticatedUser)
    {
        // /user/repos returns the authenticated user's repos (including private ones).
        // /users/{username}/repos returns a public listing for other users.
        string urlTemplate = isAuthenticatedUser
            ? $"{_cfg.BaseUrl}/user/repos?type=owner&per_page=100&page={{0}}"
            : $"{_cfg.BaseUrl}/users/{Uri.EscapeDataString(username)}/repos?per_page=100&page={{0}}";

        await foreach (var repo in PaginateAsync(urlTemplate, username))
            yield return repo;
    }

    private async IAsyncEnumerable<RepoInfo> GetOrgReposAsync(string org)
    {
        string urlTemplate = $"{_cfg.BaseUrl}/orgs/{Uri.EscapeDataString(org)}/repos?per_page=100&page={{0}}";
        await foreach (var repo in PaginateAsync(urlTemplate, org))
            yield return repo;
    }

    private async IAsyncEnumerable<RepoInfo> PaginateAsync(string urlTemplate, string owner)
    {
        for (int page = 1; ; page++)
        {
            var url = string.Format(urlTemplate, page);
            List<JsonElement>? items;

            try
            {
                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                items = doc.RootElement.EnumerateArray().ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GitHub: failed to fetch page {page} from {url}.", page, url);
                yield break;
            }

            if (items.Count == 0)
                yield break;

            foreach (var item in items)
                yield return ParseRepo(item, owner);

            if (items.Count < 100)
                yield break;

            // Be a courteous API client
            await Task.Delay(300);
        }
    }

    // -------------------------------------------------------------------------
    // Mapping
    // -------------------------------------------------------------------------

    private RepoInfo ParseRepo(JsonElement repo, string owner)
    {
        var name = repo.GetProperty("name").GetString() ?? "";
        var fullName = repo.GetProperty("full_name").GetString() ?? $"{owner}/{name}";
        var cloneUrl = repo.GetProperty("clone_url").GetString() ?? "";
        var sshUrl = repo.TryGetProperty("ssh_url", out var p) ? p.GetString() : null;
        var defaultBranch = repo.TryGetProperty("default_branch", out p) ? p.GetString() ?? "main" : "main";
        var isPrivate = repo.TryGetProperty("private", out p) && p.GetBoolean();
        var description = repo.TryGetProperty("description", out p) ? p.GetString() : null;

        string? forkOf = null;
        if (repo.TryGetProperty("fork", out p) && p.GetBoolean())
        {
            // The parent field is only present when fetching a single repo; for listings
            // it may be absent. We record the known URL from the fork's clone_url if available.
            if (repo.TryGetProperty("parent", out var parent) &&
                parent.TryGetProperty("clone_url", out var parentUrl))
            {
                forkOf = parentUrl.GetString();
            }
        }

        // Derive the actual owner from full_name (handles org repos correctly)
        var parts = fullName.Split('/');
        var actualOwner = parts.Length >= 2 ? parts[0] : owner;

        // LocalPath is assigned later by MigrationRunner.AssignLocalPaths() based on the
        // flat layout (targetFolder/<name>) with collision-resolution and git-history grouping.

        return new RepoInfo
        {
            Id = $"github:{fullName}",
            SourceType = "github",
            SourceHost = "github.com",
            Name = name,
            FullName = fullName,
            CloneUrl = cloneUrl,
            SshUrl = sshUrl,
            LocalPath = "",    // assigned by runner
            ForkOf = forkOf,
            DefaultBranch = defaultBranch,
            IsPrivate = isPrivate,
            Description = description
        };
    }
}
