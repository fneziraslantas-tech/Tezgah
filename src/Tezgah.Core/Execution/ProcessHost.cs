using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.Core.Execution;

/// <summary>Starts long-running processes (dev servers, scripts) inside a Job Object with captured output.</summary>
internal sealed class ProcessHost : IProcessHost
{
    /// <summary>Added unless the profile sets them: unbuffered UTF-8 output from Python so lines appear live and decode correctly.</summary>
    private static readonly KeyValuePair<string, string>[] DefaultEnvironment =
    [
        new("PYTHONUNBUFFERED", "1"),
        new("PYTHONIOENCODING", "utf-8"),
    ];

    public IRunningProcess Start(string itemId, RunProfile profile)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        ArgumentNullException.ThrowIfNull(profile);
        var spec = profile.Command;
        if (!Directory.Exists(spec.WorkingDirectory))
        {
            throw new DirectoryNotFoundException(L.T($"Çalışma klasörü yok: {spec.WorkingDirectory}", $"Working directory does not exist: {spec.WorkingDirectory}"));
        }

        var resolved = ProcessLaunch.Resolve(spec.FileName, spec.WorkingDirectory, spec.Environment)
            ?? throw new FileNotFoundException(L.T($"Program bulunamadı: {spec.FileName}", $"Executable not found: {spec.FileName}"), spec.FileName);
        var psi = ProcessLaunch.CreateStartInfo(resolved, spec, redirectInput: true, DefaultEnvironment);
        return RunningProcess.Start(itemId, profile, psi);
    }
}
