using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.Core.Discovery.Detectors;

/// <summary>
/// Launcher scripts at the project root (run.bat, start.bat, webui.bat, run.ps1, start.ps1...).
/// Batch files run through <c>cmd.exe /d /c &lt;full path&gt;</c> only when the path contains no cmd metacharacters;
/// PowerShell scripts run through <c>pwsh -NoProfile -ExecutionPolicy Bypass -File &lt;path&gt;</c> (Windows PowerShell as fallback).
/// </summary>
internal sealed class ScriptDetector : IProjectDetector
{
    private static readonly string[] RunScripts =
    [
        "run.bat", "start.bat", "run.cmd", "start.cmd", "webui.bat", "webui-user.bat", "launch.bat",
        "run.ps1", "start.ps1", "launch.ps1",
    ];

    private static readonly string[] SetupScripts = ["install.bat", "setup.bat", "install.ps1", "setup.ps1"];

    private readonly Lazy<string> _powerShell = new(() => Cli.ResolveExecutable("pwsh") is not null ? "pwsh" : "powershell.exe");

    public string ProjectType => "scripts";

    public ProjectDetection? Detect(string directory)
    {
        var profiles = new List<RunProfile>();
        foreach (var name in RunScripts)
        {
            var p = Profile(directory, name, RunProfileKind.Run, isDefault: profiles.Count == 0);
            if (p is not null)
            {
                profiles.Add(p);
            }
        }

        foreach (var name in SetupScripts)
        {
            var p = Profile(directory, name, RunProfileKind.Setup, isDefault: false);
            if (p is not null)
            {
                profiles.Add(p);
            }
        }

        return profiles.Count == 0 ? null : new ProjectDetection(ProjectType, profiles, [], null);
    }

    private RunProfile? Profile(string directory, string name, RunProfileKind kind, bool isDefault)
    {
        var full = Path.Combine(directory, name);
        if (!File.Exists(full))
        {
            return null;
        }

        var id = "script:" + name.ToLowerInvariant();
        if (name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return FsUtil.Profile(id, name, kind, ProjectType,
                FsUtil.Spec(_powerShell.Value, directory, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", full), isDefault);
        }

        if (!Cli.IsSafeForCmd(full))
        {
            return null; // path cannot be passed to cmd.exe safely
        }

        return FsUtil.Profile(id, name, kind, ProjectType, FsUtil.Spec("cmd.exe", directory, "/d", "/c", full), isDefault);
    }
}
