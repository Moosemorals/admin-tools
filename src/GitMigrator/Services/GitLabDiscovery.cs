using System.Net.Http.Headers;
using System.Text.Json;
using GitMigrator.Models;
using Microsoft.Extensions.Logging;

namespace GitMigrator.Services;

/// <summary>
/// Discovers repositories from the GitLab REST API (v4).
/// Works with GitLab.com and self-hosted instances.
/// </summary>
public class GitLabDiscovery
{
    private readonly GitLabSourceConfig _cfg;
    private readonly HttpClient _http;
    private readonly ILogger<GitLabDiscovery> _logger;

    public GitLabDiscovery(
        GitLabSourceConfig cfg,
        HttpClient http,
        ILogger<GitLabDiscovery> logger)
    {
        _cfg = cfg;
        _http = http;
        _logger = logger;

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", cfg.Token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GitMigrator/1.0");
    }

    private string ApiBase => _cfg.BaseUrl.TrimEnd('/') + "/api/v4";

    public async Task<List<RepoInfo>> DiscoverReposAsync()
    {
        var repos = new List<RepoInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Determine which users to query
        int? authenticatedUserId = null;
        string? authenticatedUsername = null;
        try
        {
            var userJson = await _http.GetStringAsync($"{ApiBase}/user");
            using var userDoc = JsonDocument.Parse(userJson);
            authenticatedUsername = userDoc.RootElement.GetProperty("username").GetString();
            authenticatedUserId = userDoc.RootElement.GetProperty("id").GetInt32();
            _logger.LogInformation("GitLab: authenticated as '{user}' (id={id}).", authenticatedUsername, authenticatedUserId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitLab: could not retrieve authenticated user info.");
        }

        var users = _cfg.Users.Count > 0 ? _cfg.Users
                    : authenticatedUsername is not null ? new List<string> { authenticatedUsername }
                    : new List<string>();

        foreach (var user in users)
        {
            _logger.LogInformation("GitLab: discovering repos for user '{user}'...", user);
            bool isSelf = string.Equals(user, authenticatedUsername, StringComparison.OrdinalIgnoreCase);
            await foreach (var repo in GetUserProjectsAsync(user, isSelf ? authenticatedUserId : null))
            {
                if (seen.Add(repo.CloneUrl))
                    repos.Add(repo);
            }
        }

        foreach (var group in _cfg.Groups)
        {
            _logger.LogInformation("GitLab: discovering repos for group '{group}'...", group);
            await foreach (var repo in GetGroupProjectsAsync(group))
            {
                if (seen.Add(repo.CloneUrl))
                    repos.Add(repo);
            }
        }

        _logger.LogInformation("GitLab: found {count} repo(s).", repos.Count);
        return repos;
    }

    // -------------------------------------------------------------------------
    // Pagination helpers
    // -------------------------------------------------------------------------

    private async IAsyncEnumerable<RepoInfo> GetUserProjectsAsync(string username, int? userId)
    {
        // Use /users/{id}/projects when we know the id (avoids ambiguity with namespaces)
        string urlTemplate = userId.HasValue
            ? $"{ApiBase}/users/{userId}/projects?owned=true&per_page=100&page={{0}}"
            : $"{ApiBase}/users/{Uri.EscapeDataString(username)}/projects?owned=true&per_page=100&page={{0}}";

        await foreach (var repo in PaginateAsync(urlTemplate))
            yield return repo;
    }

    private async IAsyncEnumerable<RepoInfo> GetGroupProjectsAsync(string groupPath)
    {
        var escaped = Uri.EscapeDataString(groupPath);
        var urlTemplate = $"{ApiBase}/groups/{escaped}/projects?include_subgroups=true&per_page=100&page={{0}}";
        await foreach (var repo in PaginateAsync(urlTemplate))
            yield return repo;
    }

    private async IAsyncEnumerable<RepoInfo> PaginateAsync(string urlTemplate)
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
                _logger.LogError(ex, "GitLab: failed to fetch page {page} from {url}.", page, url);
                yield break;
            }

            if (items.Count == 0)
                yield break;

            foreach (var item in items)
                yield return ParseProject(item);

            if (items.Count < 100)
                yield break;

            await Task.Delay(300);
        }
    }

    // -------------------------------------------------------------------------
    // Mapping
    // -------------------------------------------------------------------------

    private RepoInfo ParseProject(JsonElement project)
    {
        var name = project.GetProperty("name").GetString() ?? "";
        var path = project.GetProperty("path").GetString() ?? name; // URL-safe slug
        var nameWithNamespace = project.TryGetProperty("path_with_namespace", out var p) ? p.GetString() ?? path : path;
        var cloneUrl = project.TryGetProperty("http_url_to_repo", out p) ? p.GetString() ?? "" : "";
        var sshUrl = project.TryGetProperty("ssh_url_to_repo", out p) ? p.GetString() : null;
        var defaultBranch = project.TryGetProperty("default_branch", out p) ? p.GetString() ?? "main" : "main";
        var isPrivate = project.TryGetProperty("visibility", out p) &&
                        string.Equals(p.GetString(), "private", StringComparison.OrdinalIgnoreCase);
        var description = project.TryGetProperty("description", out p) ? p.GetString() : null;

        string? forkOf = null;
        if (project.TryGetProperty("forked_from_project", out var parent))
        {
            forkOf = parent.TryGetProperty("http_url_to_repo", out var pu) ? pu.GetString() : null;
        }

        // nameWithNamespace is "group/subgroup/project" or "user/project"
        // LocalPath is assigned later by MigrationRunner.AssignLocalPaths().

        return new RepoInfo
        {
            Id = $"gitlab:{nameWithNamespace}",
            SourceType = "gitlab",
            SourceHost = new Uri(_cfg.BaseUrl).Host.ToLowerInvariant(),
            Name = path,
            FullName = nameWithNamespace,
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
