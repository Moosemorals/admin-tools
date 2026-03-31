using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitMigrator.Models;

public class MigrationConfig
{
    /// <summary>Top-level folder where all repos will be cloned.</summary>
    [JsonPropertyName("targetFolder")]
    public string TargetFolder { get; set; } = "";

    /// <summary>Path for the JSON state file. Defaults to .migration-state.json inside targetFolder.</summary>
    [JsonPropertyName("stateFile")]
    public string? StateFile { get; set; }

    /// <summary>When true, log what would happen but do not actually clone or modify anything.</summary>
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; } = false;

    /// <summary>Prefer SSH URLs for cloning GitHub/GitLab repos (requires SSH keys to be configured).</summary>
    [JsonPropertyName("preferSsh")]
    public bool PreferSsh { get; set; } = false;

    [JsonPropertyName("sources")]
    [JsonConverter(typeof(SourceConfigListConverter))]
    public List<SourceConfig> Sources { get; set; } = new();
}

public abstract class SourceConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

public class GitHubSourceConfig : SourceConfig
{
    /// <summary>Personal access token (classic) or fine-grained PAT.</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    /// <summary>GitHub usernames whose repos to migrate. If empty, the authenticated user is used.</summary>
    [JsonPropertyName("users")]
    public List<string> Users { get; set; } = new();

    /// <summary>GitHub organisation names whose repos to migrate.</summary>
    [JsonPropertyName("orgs")]
    public List<string> Orgs { get; set; } = new();

    /// <summary>GitHub API base URL. Override for GitHub Enterprise Server.</summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "https://api.github.com";
}

public class GitLabSourceConfig : SourceConfig
{
    /// <summary>Personal access token with read_api and read_repository scopes.</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    /// <summary>GitLab instance base URL (no /api/v4 suffix).</summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "https://gitlab.com";

    /// <summary>GitLab usernames whose repos to migrate. If empty, the authenticated user is used.</summary>
    [JsonPropertyName("users")]
    public List<string> Users { get; set; } = new();

    /// <summary>GitLab group paths (full path, e.g. "myorg/subgroup") whose repos to migrate.</summary>
    [JsonPropertyName("groups")]
    public List<string> Groups { get; set; } = new();
}

public class LanSourceConfig : SourceConfig
{
    /// <summary>Hostname or IP of the LAN machine.</summary>
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    /// <summary>SSH user. If omitted, the default SSH user for this host is used.</summary>
    [JsonPropertyName("user")]
    public string? User { get; set; }

    /// <summary>SSH port. Defaults to 22.</summary>
    [JsonPropertyName("sshPort")]
    public int SshPort { get; set; } = 22;

    /// <summary>Directories on the LAN machine to scan recursively for git repos.</summary>
    [JsonPropertyName("scanPaths")]
    public List<string> ScanPaths { get; set; } = new();

    /// <summary>Explicit absolute paths to git repos on the LAN machine (no scanning needed).</summary>
    [JsonPropertyName("repos")]
    public List<string> Repos { get; set; } = new();

    /// <summary>Maximum directory depth when scanning for repos. Defaults to 5.</summary>
    [JsonPropertyName("maxScanDepth")]
    public int MaxScanDepth { get; set; } = 5;

    /// <summary>Returns the SSH user@host prefix, or just host if no user is specified.</summary>
    public string SshTarget => User is { Length: > 0 } u ? $"{u}@{Host}" : Host;
}

/// <summary>
/// Custom converter that reads the "type" discriminator field and delegates to the correct subclass.
/// The standard [JsonPolymorphic] approach requires .NET 7+ which we have, but it uses "__type"
/// internally; this converter preserves "type" as the discriminator field name in the JSON.
/// </summary>
internal sealed class SourceConfigListConverter : JsonConverter<List<SourceConfig>>
{
    private static readonly JsonSerializerOptions s_innerOptions = BuildInnerOptions();

    private static JsonSerializerOptions BuildInnerOptions()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return opts;
    }

    public override List<SourceConfig> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new List<SourceConfig>();
        using var doc = JsonDocument.ParseValue(ref reader);
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("type", out var typeProp))
                throw new JsonException("Source config is missing required 'type' field.");

            var type = typeProp.GetString()?.ToLowerInvariant()
                ?? throw new JsonException("Source 'type' must be a string.");

            SourceConfig? config = type switch
            {
                "github" => JsonSerializer.Deserialize<GitHubSourceConfig>(element.GetRawText(), options),
                "gitlab" => JsonSerializer.Deserialize<GitLabSourceConfig>(element.GetRawText(), options),
                "lan"    => JsonSerializer.Deserialize<LanSourceConfig>(element.GetRawText(), options),
                _        => throw new JsonException($"Unknown source type '{type}'. Supported values: github, gitlab, lan.")
            };

            if (config is not null)
                result.Add(config);
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<SourceConfig> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
            JsonSerializer.Serialize(writer, item, item.GetType(), options);
        writer.WriteEndArray();
    }
}
