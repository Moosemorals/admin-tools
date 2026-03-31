using System.Text.Json.Serialization;

namespace GitMigrator.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationPhase
{
    Initial,
    Discovering,
    Cloning,
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

    /// <summary>Planned and completed remote updates / additions.</summary>
    [JsonPropertyName("remoteUpdates")]
    public List<RemoteUpdateState> RemoteUpdates { get; set; } = new();
}
