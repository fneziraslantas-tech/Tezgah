using Tezgah.Core.Model;

namespace Tezgah.Core.Abstractions;

// ───────────────────────────── Discovery layer ─────────────────────────────

/// <summary>Finds items of one family (filesystem repos, registry apps, pipx tools...).</summary>
public interface IDiscoveryProvider
{
    /// <summary>One of <see cref="Sources"/>.</summary>
    string Id { get; }

    /// <summary>False when the backing tool (pipx, cargo...) is not installed; provider is then skipped.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);

    Task<IReadOnlyList<CatalogItem>> DiscoverAsync(TezgahSettings settings, IProgress<ScanProgress>? progress, CancellationToken ct);
}

/// <summary>Result of inspecting one folder for a particular ecosystem.</summary>
public sealed record ProjectDetection(
    string ProjectType,
    IReadOnlyList<RunProfile> Profiles,
    IReadOnlyList<ArtifactDir> Artifacts,
    string? Description);

/// <summary>Recognises one ecosystem (node, python, dotnet...) in a folder. Must be side-effect free and never execute project code.</summary>
public interface IProjectDetector
{
    string ProjectType { get; }

    /// <summary>Returns null when the folder is not this kind of project.</summary>
    ProjectDetection? Detect(string directory);
}

public interface IGitClient
{
    Task<bool> IsAvailableAsync(CancellationToken ct);

    /// <summary>Reads git state. When <paramref name="fetch"/> is true performs a network fetch first (for Behind counts).</summary>
    Task<GitInfo?> GetInfoAsync(string repoPath, bool fetch, CancellationToken ct);

    /// <summary>Fast-forward only pull.</summary>
    Task<CommandResult> PullAsync(string repoPath, CancellationToken ct);
}

// ───────────────────────────── Execution layer ─────────────────────────────

/// <summary>Runs short commands and captures output. No shell, ArgumentList only.</summary>
public interface ICommandExecutor
{
    Task<CommandResult> RunAsync(CommandSpec spec, TimeSpan timeout, CancellationToken ct);

    /// <summary>Resolves an executable name against PATH/PATHEXT. Null if not found.</summary>
    string? ResolveExecutable(string name);
}

/// <summary>A long-running process started by Tezgah. The whole process tree is bound to a Job Object.</summary>
public interface IRunningProcess : IAsyncDisposable
{
    Guid RunId { get; }
    string ItemId { get; }
    RunProfile Profile { get; }
    int ProcessId { get; }
    DateTimeOffset StartedAt { get; }
    bool HasExited { get; }
    int? ExitCode { get; }

    /// <summary>Snapshot of buffered output (bounded ring buffer).</summary>
    IReadOnlyList<OutputLine> Output { get; }

    /// <summary>http(s)://localhost URLs seen in the output (dev servers).</summary>
    IReadOnlyList<Uri> DetectedUrls { get; }

    event EventHandler<OutputLine>? OutputReceived;
    event EventHandler<Uri>? UrlDetected;
    event EventHandler? Exited;

    /// <summary>Writes a line to stdin.</summary>
    Task SendInputAsync(string line, CancellationToken ct);

    /// <summary>Graceful stop (Ctrl+C / close) then terminates the whole job after <paramref name="grace"/>.</summary>
    Task StopAsync(TimeSpan grace, CancellationToken ct);
}

public interface IProcessHost
{
    IRunningProcess Start(string itemId, RunProfile profile);
}

/// <summary>Central safety gate for every filesystem action. See SECURITY.md.</summary>
public interface IPathGuard
{
    PathCheck Check(string path, PathOperation operation);
}

/// <summary>Moves to Recycle Bin or deletes. Must re-check the path guard immediately before acting.</summary>
public interface IFileRemover
{
    Task RemoveDirectoryAsync(string path, bool toRecycleBin, CancellationToken ct);
    Task RemoveFileAsync(string path, bool toRecycleBin, CancellationToken ct);
}

// ───────────────────────────── Persistence ─────────────────────────────

public interface ICatalogStore
{
    Task<CatalogState> LoadAsync(CancellationToken ct);

    /// <summary>Atomic write (temp file + replace).</summary>
    Task SaveAsync(CatalogState state, CancellationToken ct);
}

// ───────────────────────────── Application services (used by the UI) ─────────────────────────────

/// <summary>Owns the in-memory catalog: scanning, merging, user metadata, settings.</summary>
public interface ICatalogService
{
    CatalogState State { get; }
    bool IsScanning { get; }

    /// <summary>Raised (on any thread) whenever State changes.</summary>
    event EventHandler? Changed;

    Task InitializeAsync(CancellationToken ct);

    /// <summary>Full scan with all available providers. Preserves user metadata.</summary>
    Task ScanAsync(IProgress<ScanProgress>? progress, CancellationToken ct);

    /// <summary>Re-inspects a single item (after run/pull/clean).</summary>
    Task RefreshItemAsync(string itemId, bool fetchGit, CancellationToken ct);

    Task UpdateMetaAsync(string itemId, Func<UserMeta, UserMeta> update, CancellationToken ct);
    Task UpdateSettingsAsync(Func<TezgahSettings, TezgahSettings> update, CancellationToken ct);
    Task RemoveItemAsync(string itemId, CancellationToken ct);

    /// <summary>Adds a folder the user picked manually (drag &amp; drop / "Add folder").</summary>
    Task<CatalogItem?> AddFolderAsync(string path, CancellationToken ct);

    /// <summary>Clones a git URL into <paramref name="targetParent"/> and adds it.</summary>
    Task<CatalogItem?> CloneAsync(string gitUrl, string targetParent, IProgress<string>? progress, CancellationToken ct);

    UserMeta GetMeta(string itemId);

    /// <summary>Detected profiles + user custom profiles.</summary>
    IReadOnlyList<RunProfile> GetProfiles(string itemId);
    bool IsForgotten(CatalogItem item);
}

/// <summary>Tracks processes the user started from Tezgah.</summary>
public interface IRunService
{
    IReadOnlyList<IRunningProcess> Running { get; }
    event EventHandler? RunningChanged;

    /// <summary>Starts a profile. Throws <see cref="UntrustedItemException"/> if the item is not trusted yet.</summary>
    Task<IRunningProcess> StartAsync(string itemId, RunProfile profile, CancellationToken ct);
    Task StopAsync(Guid runId, CancellationToken ct);
    Task StopAllAsync(CancellationToken ct);
    IReadOnlyList<IRunningProcess> ForItem(string itemId);
}

public interface IUninstallService
{
    Task<UninstallPlan> PlanAsync(string itemId, CancellationToken ct);

    /// <summary>
    /// Executes the plan. <paramref name="typedConfirmation"/> must equal the item name when the plan
    /// <see cref="UninstallPlan.RequiresTypedConfirmation"/>. Skips optional steps not in <paramref name="selectedSteps"/>.
    /// </summary>
    Task<UninstallResult> ExecuteAsync(UninstallPlan plan, IReadOnlyCollection<UninstallStep> selectedSteps, string? typedConfirmation, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>Frees space by deleting regenerable artifact folders (node_modules, .venv, bin/obj...).</summary>
public interface ICleanupService
{
    Task<long> CleanArtifactsAsync(string itemId, IReadOnlyCollection<ArtifactDir> artifacts, CancellationToken ct);
}

public sealed class UntrustedItemException(string itemId)
    : InvalidOperationException($"Item '{itemId}' is not trusted. Ask the user to confirm before executing its code.")
{
    public string ItemId { get; } = itemId;
}
