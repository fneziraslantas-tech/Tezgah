namespace Tezgah.Core.Safety;

/// <summary>The protected locations the <see cref="PathGuard"/> enforces. Built from the environment by default; injectable for tests.</summary>
internal sealed record PathGuardOptions
{
    /// <summary>Never deleted, nor anything inside them, nor any of their ancestors (Windows dir, Tezgah's own folders).</summary>
    public IReadOnlyList<string> ProtectedTrees { get; init; } = [];

    /// <summary>Never deleted themselves, nor their ancestors; their contents may be (user profile, Desktop, Documents...).</summary>
    public IReadOnlyList<string> ProtectedFolders { get; init; } = [];

    /// <summary>
    /// Machine-wide install roots (Program Files, Program Files (x86), ProgramData). Never deleted themselves;
    /// children only for installed-app leftovers (<see cref="DeletePolicy.AllowProgramFilesChild"/>).
    /// </summary>
    public IReadOnlyList<string> InstallRoots { get; init; } = [];

    /// <summary>Folder whose direct children are user profiles (normally C:\Users). Profiles of other users are never deleted.</summary>
    public string? ProfilesRoot { get; init; }

    public static PathGuardOptions FromEnvironment()
    {
        var trees = new List<string>();
        var folders = new List<string>();
        var installRoots = new List<string>();

        void Add(List<string> list, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
            {
                list.Add(path);
            }
        }

        Add(trees, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(trees, Environment.GetEnvironmentVariable("SystemRoot"));
        Add(trees, Environment.SystemDirectory);
        Add(trees, AppContext.BaseDirectory);
        if (Environment.ProcessPath is { } processPath)
        {
            Add(trees, Path.GetDirectoryName(processPath));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (localAppData.Length > 0)
        {
            Add(trees, Path.Combine(localAppData, "Tezgah"));
        }

        Add(installRoots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(installRoots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(installRoots, Environment.GetEnvironmentVariable("ProgramW6432"));
        Add(installRoots, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(folders, profile);
        if (profile.Length > 0)
        {
            string[] wellKnown =
            [
                "Desktop", "Documents", "Downloads", "AppData", @"AppData\Local", @"AppData\Roaming", @"AppData\LocalLow",
                @"AppData\Local\Temp", @"AppData\Local\Programs", @"AppData\Roaming\Microsoft", @"AppData\Local\Microsoft",
                ".ssh", ".gnupg", ".config", ".aws", ".azure", ".kube", ".docker", "OneDrive", "Pictures", "Music", "Videos",
                "Favorites", "Contacts", "Links", "Saved Games", "Searches", "source", @"source\repos", "go", ".virtualenvs",
            ];
            foreach (var child in wellKnown)
            {
                Add(folders, Path.Combine(profile, child));
            }
        }

        Environment.SpecialFolder[] specials =
        [
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.StartMenu,
            Environment.SpecialFolder.Programs, Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Fonts,
        ];
        foreach (var special in specials)
        {
            Add(folders, Environment.GetFolderPath(special));
        }

        Add(folders, Path.GetTempPath());
        Add(folders, Environment.GetEnvironmentVariable("OneDrive"));
        Add(folders, Environment.GetEnvironmentVariable("OneDriveConsumer"));
        Add(folders, Environment.GetEnvironmentVariable("OneDriveCommercial"));
        Add(folders, Environment.GetEnvironmentVariable("PUBLIC"));

        return new PathGuardOptions
        {
            ProtectedTrees = trees,
            ProtectedFolders = folders,
            InstallRoots = installRoots,
            ProfilesRoot = profile.Length > 0 ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(profile)) : null,
        };
    }
}

/// <summary>Extra permissions for a single delete check.</summary>
/// <param name="AllowProgramFilesChild">Allow deleting a folder below Program Files / ProgramData (installed-app leftovers only).</param>
internal readonly record struct DeletePolicy(bool AllowProgramFilesChild)
{
    public static DeletePolicy Default => default;

    public static DeletePolicy InstalledAppLeftovers => new(AllowProgramFilesChild: true);
}
