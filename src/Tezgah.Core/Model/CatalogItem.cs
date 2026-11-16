namespace Tezgah.Core.Model;

/// <summary>What kind of thing a catalog entry represents.</summary>
public enum ItemKind
{
    /// <summary>A folder containing a <c>.git</c> directory (usually cloned from GitHub).</summary>
    GitRepository,

    /// <summary>A recognised project folder without git (e.g. an extracted zip from GitHub).</summary>
    ProjectFolder,

    /// <summary>A program registered in Windows "Apps &amp; features" (registry Uninstall keys).</summary>
    InstalledApp,

    /// <summary>A command-line tool installed through a package manager (pipx, npm -g, cargo, dotnet tool, scoop...).</summary>
    PackageTool,
}

/// <summary>Well-known provider identifiers. Providers set <see cref="CatalogItem.Source"/> to one of these.</summary>
public static class Sources
{
    public const string FileSystem = "fs";
    public const string Registry = "registry";
    public const string Pipx = "pipx";
    public const string NpmGlobal = "npm";
    public const string Cargo = "cargo";
    public const string DotnetTool = "dotnet-tool";
    public const string Scoop = "scoop";
    public const string GoBin = "go";
}

/// <summary>
/// One discovered thing on the machine. Immutable snapshot produced by a discovery provider;
/// user-editable data (tags, notes, trust...) lives separately in <see cref="UserMeta"/>.
/// </summary>
public sealed record CatalogItem
{
    /// <summary>Stable identifier. Use <see cref="ItemIds"/> to build it so rescans map to the same id.</summary>
    public required string Id { get; init; }

    public required ItemKind Kind { get; init; }

    /// <summary>Provider id, one of <see cref="Sources"/>.</summary>
    public required string Source { get; init; }

    public required string Name { get; init; }

    /// <summary>Root folder on disk (repos/projects/install location). Null when unknown.</summary>
    public string? Path { get; init; }

    public string? Version { get; init; }

    public string? Publisher { get; init; }

    /// <summary>Short description (package.json description, README first paragraph, registry comment...).</summary>
    public string? Description { get; init; }

    public DateTimeOffset? InstalledAt { get; init; }

    /// <summary>Last meaningful activity: last commit, last file write, or last run - whichever is newest.</summary>
    public DateTimeOffset? LastActivity { get; init; }

    /// <summary>Total size on disk in bytes, if computed.</summary>
    public long? SizeBytes { get; init; }

    public GitInfo? Git { get; init; }

    /// <summary>Detected ecosystems, e.g. "node", "python", "dotnet", "rust", "go", "java", "docker", "static".</summary>
    public IReadOnlyList<string> ProjectTypes { get; init; } = [];

    /// <summary>Ready-to-run commands (run / setup / build / test) detected for this item.</summary>
    public IReadOnlyList<RunProfile> RunProfiles { get; init; } = [];

    /// <summary>Regenerable folders (node_modules, .venv, bin/obj, target...) that can be cleaned to free space.</summary>
    public IReadOnlyList<ArtifactDir> Artifacts { get; init; } = [];

    /// <summary>How to remove this item via its own uninstaller / package manager. Null for plain folders.</summary>
    public CommandSpec? UninstallCommand { get; init; }

    /// <summary>Optional homepage / repository web URL (GitHub page, publisher site).</summary>
    public string? WebUrl { get; init; }
}

/// <summary>Snapshot of a repository's git state.</summary>
public sealed record GitInfo
{
    public string? RemoteUrl { get; init; }
    public string? Branch { get; init; }
    public bool HasUpstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public int ModifiedFiles { get; init; }
    public int UntrackedFiles { get; init; }
    public int Stashes { get; init; }
    /// <summary>Committer time of HEAD's commit; null when unknown (e.g. an untrusted repository whose HEAD commit is packed).</summary>
    public DateTimeOffset? LastCommitAt { get; init; }

    public string? LastCommitSubject { get; init; }

    private readonly bool? _hasCommits;

    /// <summary>HEAD points at a commit (not an unborn branch). Defaults to <c>LastCommitAt is not null</c> when not set.</summary>
    public bool HasCommits
    {
        get => _hasCommits ?? LastCommitAt is not null;
        init => _hasCommits = value;
    }

    /// <summary>
    /// Commits reachable from any local branch (not only HEAD) but from no remote-tracking ref. Null when it could not be
    /// determined (treated as unsaved work: fail closed).
    /// </summary>
    public int? UnpushedCommits { get; init; } = 0;

    /// <summary>Local branches that hold work no remote has (no upstream, ahead of it, or upstream deleted). Capped list.</summary>
    public IReadOnlyList<string> UnpushedBranches { get; init; } = [];

    /// <summary>The checked-out branch tracks an upstream branch that no longer exists ("[gone]").</summary>
    public bool UpstreamGone { get; init; }

    /// <summary>Linked worktrees of this (main) repository; deleting it breaks them.</summary>
    public IReadOnlyList<string> LinkedWorktrees { get; init; } = [];

    /// <summary>
    /// The working tree was not inspected (git was not run: untrusted repository or one that could not be validated for
    /// the git CLI), so <see cref="ModifiedFiles"/>/<see cref="UntrackedFiles"/> are not known. Counts as unsaved work.
    /// </summary>
    public bool StatusUnknown { get; init; }

    /// <summary>Unsaved work that was actually observed (for display; deletion safety uses <see cref="HasUnsavedWork"/>).</summary>
    public bool HasKnownUnsavedWork =>
        ModifiedFiles > 0 || UntrackedFiles > 0 || Stashes > 0 || Ahead > 0 || ((!HasUpstream || UpstreamGone) && HasCommits) ||
        UnpushedCommits > 0 || LinkedWorktrees.Count > 0;

    /// <summary>True when deleting the folder would (or, with unknown state, might) lose work that exists nowhere else.</summary>
    public bool HasUnsavedWork => HasKnownUnsavedWork || UnpushedCommits is null || StatusUnknown;
}

/// <summary>
/// A regenerable directory inside a project. <paramref name="Ambiguous"/> marks names that are often hand-made or
/// committed (build, dist, out): never pre-selected, and refused for cleanup when git tracks or does not ignore files in them.
/// </summary>
public sealed record ArtifactDir(string Path, string Kind, long SizeBytes, bool Ambiguous = false)
{
    /// <summary>Folder names that are build output for some tools but source/resources for others.</summary>
    public static readonly IReadOnlySet<string> AmbiguousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "build", "dist", "out" };
}

/// <summary>Builds stable ids. Paths are normalised (full path, lower-case, no trailing separator).</summary>
public static class ItemIds
{
    public static string ForPath(string path) =>
        "path:" + System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)).ToLowerInvariant();

    public static string ForSource(string source, string key) => $"{source}:{key.ToLowerInvariant()}";
}
