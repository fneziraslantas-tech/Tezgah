using Tezgah.Core.Model;
using Tezgah.Core.Safety;
using Tezgah.Core.Tests.Execution;

namespace Tezgah.Core.Tests.Safety;

public class PathGuardTests
{
    private static readonly PathGuard Guard = PathGuard.Default;

    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static string Drive => Path.GetPathRoot(Win)!;

    public static TheoryData<string> DeniedForDelete()
    {
        var data = new TheoryData<string>
        {
            Drive,
            Drive.TrimEnd('\\'),
            Win,
            Win + ".",
            Win + " ",
            Win + "\\",
            Win.ToUpperInvariant(),
            Win.ToLowerInvariant(),
            Win.Replace('\\', '/'),
            Environment.SystemDirectory,
            Path.Combine(Environment.SystemDirectory, "drivers"),
            Path.Combine(Environment.SystemDirectory, "drivers", "etc"),
            Win[..2] + "/Windows\\System32",
            @"\\?\" + Win,
            @"\\?\" + Environment.SystemDirectory,
            @"\\.\" + Win,
            @"\\?\GLOBALROOT\Device\HarddiskVolume1\Windows",
            @"\\localhost\C$\Windows",
            @"\\server\share\repo",
            "//server/share/repo",
            @"\Windows",
            "Windows",
            @"..\..\Windows",
            Path.Combine(Win, "..", "Windows"),
            Path.Combine(Win, ".", "System32"),
            Drive + "PROGRA~1",
            Drive + "PROGRA~2",
            Drive + "Program Files",
            Drive + "Program Files (x86)",
            Drive + "ProgramData",
            Path.Combine(Drive + "Program Files", "Common Files"),
            Drive + "Users",
            Profile,
            Path.Combine(Profile, "Desktop"),
            Path.Combine(Profile, "Documents"),
            Path.Combine(Profile, "Downloads"),
            Path.Combine(Profile, "AppData"),
            Path.Combine(Profile, "AppData", "Local"),
            Path.Combine(Profile, "AppData", "Roaming"),
            Path.Combine(Profile, ".ssh"),
            Path.Combine(Profile, ".gnupg"),
            Path.GetTempPath(),
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "Tezgah.Core.dll"),
            Drive + "$Recycle.Bin",
            Drive + "System Volume Information",
            Drive + @"Users\Public",
            Win + @":stream",
            Drive + "*",
            "",
            "   ",
            "C:\\a\nb",
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(DeniedForDelete))]
    public void Protected_and_ambiguous_paths_are_denied_for_delete(string path)
    {
        var check = Guard.Check(path, PathOperation.Delete);
        Assert.False(check.Allowed, $"'{path}' was allowed");
        Assert.False(string.IsNullOrWhiteSpace(check.Reason));
    }

    [Fact]
    public void Short_name_of_program_files_is_denied_even_when_it_resolves()
    {
        var shortPath = Drive + "PROGRA~1";
        Assert.False(Guard.Check(shortPath, PathOperation.Delete).Allowed);
        Assert.False(Guard.Check(shortPath + "\\Common Files", PathOperation.Delete).Allowed);
    }

    [Fact]
    public void Project_folder_in_temp_is_allowed()
    {
        using var dir = new TempDir();
        var project = dir.Sub("my-project");
        Assert.True(Guard.Check(project, PathOperation.Delete).Allowed);
        Assert.True(Guard.Check(project.Replace('\\', '/'), PathOperation.Delete).Allowed);
        Assert.True(Guard.Check(project + "\\", PathOperation.Delete).Allowed);
        Assert.True(Guard.Check(project.ToUpperInvariant(), PathOperation.Delete).Allowed);
        Assert.True(Guard.Check(project, PathOperation.Execute).Allowed);
        Assert.True(Guard.Check(project, PathOperation.Read).Allowed);
    }

    [Fact]
    public void Missing_paths_are_denied()
    {
        var missing = Path.Combine(Path.GetTempPath(), "TezgahTests", "missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(Guard.Check(missing, PathOperation.Delete).Allowed);
        Assert.False(Guard.Check(missing, PathOperation.Execute).Allowed);
        Assert.False(Guard.Check(missing, PathOperation.Read).Allowed);
    }

    [Fact]
    public void Alternate_data_stream_and_trailing_dot_are_denied()
    {
        using var dir = new TempDir();
        var file = dir.File("a.txt");
        Assert.True(Guard.Check(file, PathOperation.Delete).Allowed);
        Assert.False(Guard.Check(file + ":hidden", PathOperation.Delete).Allowed);
        Assert.False(Guard.Check(file + "::$DATA", PathOperation.Delete).Allowed);
        Assert.False(Guard.Check(file + ".", PathOperation.Delete).Allowed);
        Assert.False(Guard.Check(dir.Path + "\\..\\" + Path.GetFileName(dir.Path), PathOperation.Delete).Allowed);
    }

    [Fact]
    public void Execute_is_denied_in_system_folders()
    {
        Assert.False(Guard.Check(Win, PathOperation.Execute).Allowed);
        Assert.False(Guard.Check(Environment.SystemDirectory, PathOperation.Execute).Allowed);
        Assert.False(Guard.Check(@"\\server\share", PathOperation.Execute).Allowed);
    }

    [Fact]
    public void Execute_requires_a_directory()
    {
        using var dir = new TempDir();
        Assert.False(Guard.Check(dir.File("x.txt"), PathOperation.Execute).Allowed);
    }

    [Fact]
    public void Junction_to_windows_is_denied()
    {
        using var dir = new TempDir();
        var link = Path.Combine(dir.Path, "innocent");
        if (!Junction.TryCreate(link, Win))
        {
            return; // mklink unavailable (policy); nothing to verify
        }

        Assert.False(Guard.Check(link, PathOperation.Delete).Allowed);
        Assert.False(Guard.Check(Path.Combine(link, "System32"), PathOperation.Delete).Allowed);
        Assert.False(Guard.Check(link, PathOperation.Execute).Allowed);
        Assert.False(Guard.Check(Path.Combine(link, "System32"), PathOperation.Execute).Allowed);

        // The folder containing the junction itself is fine to delete (the remover never follows the link).
        Assert.True(Guard.Check(dir.Path, PathOperation.Delete).Allowed);
    }

    [Fact]
    public void Junction_inside_project_is_runnable_but_not_deletable()
    {
        using var dir = new TempDir();
        var target = dir.Sub("real");
        var link = Path.Combine(dir.Path, "linked");
        if (!Junction.TryCreate(link, target))
        {
            return;
        }

        Assert.True(Guard.Check(link, PathOperation.Execute).Allowed);
        Assert.False(Guard.Check(link, PathOperation.Delete).Allowed);
        Assert.True(Guard.Check(target, PathOperation.Delete).Allowed);
    }

    [Fact]
    public void Program_files_children_need_the_installed_app_policy()
    {
        using var dir = new TempDir();
        var fakeProgramFiles = dir.Sub("Program Files");
        var app = dir.Sub("Program Files", "Vendor", "App");
        var guard = new PathGuard(new PathGuardOptions { InstallRoots = [fakeProgramFiles] });

        Assert.False(guard.Check(app, PathOperation.Delete).Allowed);
        Assert.True(guard.Check(app, PathOperation.Delete, DeletePolicy.InstalledAppLeftovers).Allowed);
        Assert.False(guard.Check(fakeProgramFiles, PathOperation.Delete, DeletePolicy.InstalledAppLeftovers).Allowed);
        Assert.False(guard.Check(dir.Path, PathOperation.Delete, DeletePolicy.InstalledAppLeftovers).Allowed);
    }

    [Fact]
    public void Ancestors_of_protected_folders_are_denied()
    {
        using var dir = new TempDir();
        var protectedFolder = dir.Sub("parent", "Documents");
        var guard = new PathGuard(new PathGuardOptions { ProtectedFolders = [protectedFolder] });

        Assert.False(guard.Check(protectedFolder, PathOperation.Delete).Allowed);
        Assert.False(guard.Check(Path.GetDirectoryName(protectedFolder)!, PathOperation.Delete).Allowed);
        Assert.True(guard.Check(dir.Sub("parent", "Documents", "old-project"), PathOperation.Delete).Allowed);
    }

    [Fact]
    public void Protected_trees_deny_descendants()
    {
        using var dir = new TempDir();
        var tree = dir.Sub("app");
        var guard = new PathGuard(new PathGuardOptions { ProtectedTrees = [tree] });
        Assert.False(guard.Check(dir.Sub("app", "data"), PathOperation.Delete).Allowed);
        Assert.False(guard.Check(dir.Path, PathOperation.Delete).Allowed);
    }

    [Fact]
    public void Shallow_paths_are_denied()
    {
        // Two segments (C:\something) is never deletable even when not otherwise protected.
        var guard = new PathGuard(new PathGuardOptions());
        Assert.False(guard.Check(Win, PathOperation.Delete).Allowed);
    }

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a", true)]
    [InlineData(@"C:\a", @"C:\a", true)]
    [InlineData(@"C:\ab", @"C:\a", false)]
    [InlineData(@"c:\A\B\", @"C:\a", true)]
    public void IsSameOrUnder_compares_whole_segments(string child, string parent, bool expected) =>
        Assert.Equal(expected, PathGuard.IsSameOrUnder(child, parent));
}
