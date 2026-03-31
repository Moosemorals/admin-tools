using System.Text.Json;
using GitMigrator.Models;
using Microsoft.Extensions.Logging;

namespace GitMigrator.Services;

/// <summary>
/// Loads and saves the migration state JSON file. The state file enables the migration
/// to be interrupted and resumed without repeating completed work.
/// </summary>
public class StateManager
{
    private readonly string _stateFilePath;
    private readonly ILogger<StateManager> _logger;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public MigrationState State { get; private set; } = new();

    public StateManager(string stateFilePath, ILogger<StateManager> logger)
    {
        _stateFilePath = stateFilePath;
        _logger = logger;
    }

    public void Load()
    {
        if (!File.Exists(_stateFilePath))
        {
            _logger.LogInformation("No existing state file found. Starting a fresh migration.");
            State = new MigrationState();
            return;
        }

        try
        {
            var json = File.ReadAllText(_stateFilePath);
            State = JsonSerializer.Deserialize<MigrationState>(json, s_jsonOptions) ?? new MigrationState();
            _logger.LogInformation(
                "Resumed from state file: phase={phase}, {repos} repos discovered, {cloned}/{total} cloned.",
                State.Phase,
                State.DiscoveredRepos.Count,
                State.CloneStates.Values.Count(c => c.Status == CloneStatus.Complete),
                State.CloneStates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not parse state file '{path}'. Rename or delete it to start fresh.", _stateFilePath);
            throw;
        }
    }

    public void Save()
    {
        State.LastUpdated = DateTime.UtcNow;
        var dir = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Write to a temp file and then atomically rename to avoid corruption on interrupt
        var tmp = _stateFilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(State, s_jsonOptions));
        File.Move(tmp, _stateFilePath, overwrite: true);

        _logger.LogDebug("State saved to {path}", _stateFilePath);
    }

    /// <summary>Reset discovery state so the next run re-queries all sources.</summary>
    public void ResetDiscovery()
    {
        State.DiscoveryComplete = false;
        State.DiscoveredRepos.Clear();
        State.Phase = MigrationPhase.Initial;
        // Clone states and remote updates derived from the previous discovery are also invalid
        State.CloneStates.Clear();
        State.RemoteUpdates.Clear();
        _logger.LogInformation("Discovery state reset.");
    }

    /// <summary>Reset clone state for repos that previously failed so they are retried.</summary>
    public void ResetFailedClones()
    {
        int count = 0;
        foreach (var key in State.CloneStates.Keys.ToList())
        {
            if (State.CloneStates[key].Status == CloneStatus.Failed)
            {
                State.CloneStates.Remove(key);
                count++;
            }
        }
        if (count > 0)
            _logger.LogInformation("Reset {count} failed clone(s) for retry.", count);
    }
}
