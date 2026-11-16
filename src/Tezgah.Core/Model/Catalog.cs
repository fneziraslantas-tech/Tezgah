namespace Tezgah.Core.Model;

/// <summary>User-owned data attached to an item id. Survives rescans.</summary>
public sealed record UserMeta
{
    public bool Favorite { get; init; }
    public bool Hidden { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Note { get; init; }

    /// <summary>User confirmed they trust this folder's code to be executed (workspace-trust model).</summary>
    public bool Trusted { get; init; }

    public DateTimeOffset? LastRunAt { get; init; }

    /// <summary>Profile id the user picked as default; overrides detector default.</summary>
    public string? DefaultProfileId { get; init; }

    /// <summary>User-defined extra run profiles.</summary>
    public IReadOnlyList<RunProfile> CustomProfiles { get; init; } = [];
}

public sealed record TezgahSettings
{
    /// <summary>Folders searched for repositories/projects. Defaults are filled by the discovery layer when empty.</summary>
    public IReadOnlyList<string> ScanRoots { get; init; } = [];

    /// <summary>Directory names never descended into (case-insensitive).</summary>
    public IReadOnlyList<string> ExcludedDirectoryNames { get; init; } =
    [
        "node_modules", ".venv", "venv", "env", "__pycache__", "bin", "obj", "target", "dist", "build",
        ".git", ".hg", ".svn", ".idea", ".vs", ".vscode", "AppData", "$Recycle.Bin", "Windows", "Program Files",
        "Program Files (x86)", "ProgramData", ".nuget", ".cache", ".cargo", ".rustup", ".gradle", ".m2",
    ];

    public int MaxDepth { get; init; } = 6;

    /// <summary>Items with no activity for this many days are flagged "forgotten".</summary>
    public int ForgottenAfterDays { get; init; } = 90;

    public bool IncludeInstalledApps { get; init; } = true;
    public bool IncludePackageTools { get; init; } = true;

    /// <summary>Delete to Recycle Bin (default) instead of permanently.</summary>
    public bool UseRecycleBin { get; init; } = true;

    /// <summary>"tr" or "en".</summary>
    public string Language { get; init; } = "tr";

    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; init; } = "system";
}

/// <summary>Everything persisted to disk.</summary>
public sealed record CatalogState
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset? LastScanAt { get; init; }
    public IReadOnlyList<CatalogItem> Items { get; init; } = [];
    public IReadOnlyDictionary<string, UserMeta> Meta { get; init; } = new Dictionary<string, UserMeta>();
    public TezgahSettings Settings { get; init; } = new();
}

public sealed record ScanProgress(string Stage, string? CurrentPath, int ItemsFound);
