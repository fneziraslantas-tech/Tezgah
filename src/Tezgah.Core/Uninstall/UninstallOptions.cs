namespace Tezgah.Core.Uninstall;

/// <summary>Locations the uninstall/cleanup services use. Built from the environment by default; injectable for tests.</summary>
internal sealed record UninstallOptions
{
    /// <summary>JSON-lines audit log (default %LOCALAPPDATA%\Tezgah\audit.log).</summary>
    public required string AuditLogPath { get; init; }

    /// <summary>Folders holding per-project virtualenvs created outside the project (pipenv/virtualenvwrapper, Poetry).</summary>
    public IReadOnlyList<string> VirtualenvRoots { get; init; } = [];

    /// <summary>Folders scanned for .lnk shortcuts pointing into an item (Start menu recursively, Desktop top-level).</summary>
    public IReadOnlyList<string> ShortcutRootsRecursive { get; init; } = [];

    public IReadOnlyList<string> ShortcutRootsTopLevel { get; init; } = [];

    /// <summary>Upper bound for a vendor uninstaller / package-manager removal.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public static UninstallOptions FromEnvironment()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var venvRoots = new List<string>();
        if (profile.Length > 0)
        {
            venvRoots.Add(Path.Combine(profile, ".virtualenvs"));
        }

        if (Environment.GetEnvironmentVariable("WORKON_HOME") is { Length: > 0 } workon && Path.IsPathFullyQualified(workon))
        {
            venvRoots.Add(workon);
        }

        if (localAppData.Length > 0)
        {
            venvRoots.Add(Path.Combine(localAppData, "pypoetry", "Cache", "virtualenvs"));
        }

        return new UninstallOptions
        {
            AuditLogPath = Path.Combine(localAppData, "Tezgah", "audit.log"),
            VirtualenvRoots = venvRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ShortcutRootsRecursive = [Environment.GetFolderPath(Environment.SpecialFolder.Programs)],
            ShortcutRootsTopLevel = [Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)],
        };
    }
}
