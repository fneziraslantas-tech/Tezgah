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

    /// <summary>
    /// Gitignored, untracked paths (ignored directories collapsed to one entry). Used only when planning a deletion:
    /// such files (.env, local databases, keys) exist nowhere else. Empty when unknown.
    /// </summary>
    Task<IReadOnlyList<string>> GetIgnoredPathsAsync(string repoPath, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// True when <paramref name="relativeDirectory"/> holds files git tracks, or untracked files that are not ignored
    /// (i.e. not regenerable build output). Null when it cannot be determined.
    /// </summary>
    Task<bool?> HoldsUnregenerableFilesAsync(string repoPath, string relativeDirectory, CancellationToken ct) => Task.FromResult<bool?>(null);
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

    /// <summary>
    /// Workspace trust checked at the point of use: the recorded trust decision AND, for filesystem items, the identity of
    /// the folder that lives at the path right now (a folder deleted and replaced since the last scan is not trusted).
    /// Every caller about to execute an item's code or honour its repository config must ask this, not <see cref="UserMeta.Trusted"/>.
    /// </summary>
    bool IsTrusted(string itemId) => GetMeta(itemId).Trusted;

    /// <summary>
    /// The URL Tezgah cloned this item from, but only while the folder at the path right now is the one Tezgah cloned
    /// (checked against its live identity); null otherwise. The only remote the trust prompt may call verified.
    /// </summary>
    string? VerifiedCloneSource(string itemId) => null;

    /// <summary>Detected profiles + user custom profiles.</summary>
    IReadOnlyList<RunProfile> GetProfiles(string itemId);
    bool IsForgotten(CatalogItem item);
}

/// <summary>Tracks processes the user started from Tezgah.</summary>
public interface IRunService
{
    IReadOnlyList<IRunningProcess> Running { get; }
    event EventHandler? RunningChanged;

    /// <summary>
    /// Starts the catalog's copy of the profile with <paramref name="profile"/>'s id. Throws <see cref="UntrustedItemException"/>
    /// if the item is not trusted yet, and <see cref="CommandChangedException"/> when that copy no longer is
    /// <paramref name="expectedCommand"/> — the command the user was shown (trust prompt, profile list) and agreed to run.
    /// </summary>
    Task<IRunningProcess> StartAsync(string itemId, RunProfile profile, CommandSpec expectedCommand, CancellationToken ct);
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

/// <summary>
/// The command the catalog now holds for a profile id is not the one the user was shown (the folder was re-read or
/// rescanned meanwhile, e.g. while the trust prompt was open). Nothing was started; the user has to look again.
/// </summary>
public sealed class CommandChangedException(string itemId, string profileId)
    : InvalidOperationException(L.T(
        "Bu profilin komutu siz onayladıktan sonra değişti; hiçbir şey çalıştırılmadı. Güncel komutu kontrol edip yeniden deneyin.",
        "This profile's command changed after you approved it; nothing was run. Check the current command and try again."))
{
    public string ItemId { get; } = itemId;

    public string ProfileId { get; } = profileId;
}
