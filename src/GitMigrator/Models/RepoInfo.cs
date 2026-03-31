using System.Text.Json.Serialization;

namespace GitMigrator.Models;

/// <summary>All information about a discovered repository.</summary>
public class RepoInfo
{
    /// <summary>Globally unique identifier: "{sourceType}:{fullName}".</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Source type: "github", "gitlab", or "lan".</summary>
    [JsonPropertyName("sourceType")]
    public string SourceType { get; set; } = "";

    /// <summary>Hostname / provider label (e.g. "github.com", "gitlab.com", "devbox").</summary>
    [JsonPropertyName("sourceHost")]
    public string SourceHost { get; set; } = "";

    /// <summary>Short repository name (last path segment).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Full name including owner/namespace (e.g. "user/repo").</summary>
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = "";

    /// <summary>HTTPS clone URL returned by the API.</summary>
    [JsonPropertyName("cloneUrl")]
    public string CloneUrl { get; set; } = "";

    /// <summary>SSH clone URL (may be null for LAN repos).</summary>
    [JsonPropertyName("sshUrl")]
    public string? SshUrl { get; set; }

    /// <summary>Absolute path the repo will be (or has been) cloned to on the local machine.</summary>
    [JsonPropertyName("localPath")]
    public string LocalPath { get; set; } = "";

    /// <summary>HTTPS clone URL of the parent repo if this is a fork.</summary>
    [JsonPropertyName("forkOf")]
    public string? ForkOf { get; set; }

    /// <summary>Default branch name.</summary>
    [JsonPropertyName("defaultBranch")]
    public string DefaultBranch { get; set; } = "main";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("isPrivate")]
    public bool IsPrivate { get; set; }

    /// <summary>
    /// Remote configuration that existed on the SOURCE repo (discovered via the API or SSH).
    /// Key = remote name, Value = fetch URL.
    /// Used during remote-reconciliation to wire up local paths.
    /// </summary>
    [JsonPropertyName("originalRemotes")]
    public Dictionary<string, string> OriginalRemotes { get; set; } = new();
}
