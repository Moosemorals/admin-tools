using System.Text.Json;
using GitMigrator.Models;
using GitMigrator.Services;
using Microsoft.Extensions.Logging;

// ---------------------------------------------------------------------------
// Argument parsing
// ---------------------------------------------------------------------------

bool resetDiscovery = false;
bool resetFailed = false;
bool resetAll = false;
bool dryRun = false;
bool verbose = false;
string? configPath = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--reset-discovery": resetDiscovery = true; break;
        case "--reset-failed":   resetFailed = true;    break;
        case "--reset-all":      resetAll = true;       break;
        case "--dry-run":        dryRun = true;          break;
        case "--verbose":        verbose = true;         break;
        case "--help": case "-h":
            PrintHelp();
            return 0;
        default:
            if (!args[i].StartsWith("--"))
                configPath = args[i];
            else
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                PrintHelp();
                return 1;
            }
            break;
    }
}

configPath ??= "config.json";

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config file not found: {Path.GetFullPath(configPath)}");
    Console.Error.WriteLine("Pass the path to your config file as the first argument, or create config.json in the current directory.");
    Console.Error.WriteLine("See config.example.json for the expected format.");
    return 1;
}

MigrationConfig cfg;
try
{
    var json = File.ReadAllText(configPath);
    var opts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
    cfg = JsonSerializer.Deserialize<MigrationConfig>(json, opts)
        ?? throw new InvalidOperationException("Config file deserialized to null.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to parse config file: {ex.Message}");
    return 1;
}

// Command-line --dry-run overrides the config file value
if (dryRun)
    cfg.DryRun = true;

if (string.IsNullOrWhiteSpace(cfg.TargetFolder))
{
    Console.Error.WriteLine("Config 'targetFolder' must not be empty.");
    return 1;
}

if (cfg.Sources.Count == 0)
{
    Console.Error.WriteLine("Config 'sources' must contain at least one entry.");
    return 1;
}

cfg.TargetFolder = Path.GetFullPath(cfg.TargetFolder);

var stateFilePath = cfg.StateFile is { Length: > 0 } sf
    ? Path.GetFullPath(sf)
    : Path.Combine(cfg.TargetFolder, ".migration-state.json");

// ---------------------------------------------------------------------------
// Logging
// ---------------------------------------------------------------------------

var logLevel = verbose ? LogLevel.Debug : LogLevel.Information;
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(logLevel)
        .AddSimpleConsole(c =>
        {
            c.TimestampFormat = "HH:mm:ss ";
            c.SingleLine = false;
        });
});

var rootLogger = loggerFactory.CreateLogger("GitMigrator");
rootLogger.LogInformation("GitMigrator starting.");
rootLogger.LogInformation("Config:      {path}", Path.GetFullPath(configPath));
rootLogger.LogInformation("State file:  {path}", stateFilePath);
rootLogger.LogInformation("Target:      {path}", cfg.TargetFolder);
if (cfg.DryRun)
    rootLogger.LogWarning("DRY RUN mode – no changes will be made to the filesystem.");

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

var stateManager = new StateManager(stateFilePath, loggerFactory.CreateLogger<StateManager>());
stateManager.Load();

if (resetAll)
{
    stateManager.ResetDiscovery(); // also clears clones and remotes
    rootLogger.LogInformation("State fully reset.");
}
else
{
    if (resetDiscovery)
        stateManager.ResetDiscovery();
    if (resetFailed)
        stateManager.ResetFailedClones();
}

// ---------------------------------------------------------------------------
// Run migration
// ---------------------------------------------------------------------------

if (!cfg.DryRun)
    Directory.CreateDirectory(cfg.TargetFolder);

var gitService = new GitService(loggerFactory.CreateLogger<GitService>(), cfg.DryRun);
var runner = new MigrationRunner(cfg, stateManager, gitService, loggerFactory);

try
{
    await runner.RunAsync();
}
catch (Exception ex)
{
    rootLogger.LogError(ex, "Migration aborted due to an unhandled error.");
    return 2;
}

return 0;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static void PrintHelp()
{
    Console.WriteLine("""
        GitMigrator – migrate git repositories to a local folder

        Usage:
          GitMigrator [options] [config.json]

        Arguments:
          config.json           Path to the JSON configuration file (default: ./config.json)

        Options:
          --dry-run             Print what would be done without cloning or modifying remotes
          --verbose             Enable debug-level logging
          --reset-discovery     Re-query all sources even if discovery was already completed
          --reset-failed        Retry repos whose clone previously failed
          --reset-all           Start the migration from scratch (clears all saved progress)
          --help, -h            Show this help message

        See config.example.json for the configuration file format.
        """);
}
