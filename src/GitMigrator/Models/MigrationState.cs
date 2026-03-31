using System.Text.Json.Serialization;

namespace GitMigrator.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationPhase
{
    Initial,
    Discovering,
    Cloning,
    Grouping,        // identify repos sharing git history; merge into a single canonical clone
    UpdatingRemotes,
    Complete
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CloneStatus
{
    Pending,
    Complete,
    Failed
}

public class RepoCloneState
{
    [JsonPropertyName("repoId")]
    public string RepoId { get; set; } = "";

    [JsonPropertyName("status")]
    public CloneStatus Status { get; set; } = CloneStatus.Pending;

    [JsonPropertyName("localPath")]
    public string LocalPath { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }
}

public class RemoteUpdateState
{
    [JsonPropertyName("repoId")]
    public string RepoId { get; set; } = "";

    /// <summary>Name of the remote in the LOCAL clone that is being updated/added.</summary>
    [JsonPropertyName("remoteName")]
    public string RemoteName { get; set; } = "";

    [JsonPropertyName("oldUrl")]
    public string OldUrl { get; set; } = "";

    [JsonPropertyName("newUrl")]
    public string NewUrl { get; set; } = "";

    /// <summary>True = set-url on an existing remote; False = add a new remote.</summary>
    [JsonPropertyName("isUpdate")]
    public bool IsUpdate { get; set; }

    [JsonPropertyName("isDone")]
    public bool IsDone { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// A set of repos that share git history (detected via overlapping commit SHAs).
/// All members are merged into the canonical clone; their separate clones are removed.
/// </summary>
public class RepoGroup
{
    /// <summary>RepoInfo.Id of the repo whose directory is the canonical local clone.</summary>
    [JsonPropertyName("canonicalRepoId")]
    public string CanonicalRepoId { get; set; } = "";

    /// <summary>All members of this group (canonical first).</summary>
    [JsonPropertyName("memberRepoIds")]
    public List<string> MemberRepoIds { get; set; } = new();

    /// <summary>True once all non-canonical members have been fetched into canonical.</summary>
    [JsonPropertyName("fetchComplete")]
    public bool FetchComplete { get; set; }

    /// <summary>Members whose content has already been fetched + whose directory has been removed.</summary>
    [JsonPropertyName("consolidatedMemberIds")]
    public List<string> ConsolidatedMemberIds { get; set; } = new();
}

public class MigrationState
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("phase")]
    public MigrationPhase Phase { get; set; } = MigrationPhase.Initial;

    [JsonPropertyName("lastUpdated")]
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("discoveryComplete")]
    public bool DiscoveryComplete { get; set; }

    [JsonPropertyName("discoveredRepos")]
    public List<RepoInfo> DiscoveredRepos { get; set; } = new();

    /// <summary>Per-repo clone progress. Key = RepoInfo.Id.</summary>
    [JsonPropertyName("cloneStates")]
    public Dictionary<string, RepoCloneState> CloneStates { get; set; } = new();

    /// <summary>
    /// Groups of repos that share git history.
    /// Populated during the Grouping phase. Empty list = grouping not yet run.
    /// </summary>
    [JsonPropertyName("repoGroups")]
    public List<RepoGroup> RepoGroups { get; set; } = new();

    /// <summary>True once the Grouping phase has fully completed.</summary>
    [JsonPropertyName("groupingComplete")]
    public bool GroupingComplete { get; set; }

    /// <summary>Planned and completed remote updates / additions.</summary>
    [JsonPropertyName("remoteUpdates")]
    public List<RemoteUpdateState> RemoteUpdates { get; set; } = new();
}
