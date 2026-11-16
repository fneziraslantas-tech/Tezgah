using System.ComponentModel;
using System.Diagnostics;
using Tezgah.Core.Interop;
using Tezgah.Core.Safety;

namespace Tezgah.Core.Execution;

/// <summary>A process whose executable image lives under some folder.</summary>
internal sealed record FoundProcess(int ProcessId, string ImagePath);

internal interface IProcessFinder
{
    /// <summary>Processes whose executable is inside <paramref name="folder"/>. Processes we cannot query are ignored.</summary>
    IReadOnlyList<FoundProcess> FindUnder(string folder);

    /// <summary>Kills the process if (and only if) its image is still under <paramref name="folder"/> (guards against PID reuse).</summary>
    bool TryKill(FoundProcess process, string folder);
}

/// <summary>Locates processes by image path using QueryFullProcessImageNameW (works for most processes without elevation).</summary>
internal sealed class ProcessFinder : IProcessFinder
{
    public IReadOnlyList<FoundProcess> FindUnder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var self = Environment.ProcessId;
        var result = new List<FoundProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == self || process.Id <= 4)
                {
                    continue;
                }

                var image = Win32.TryGetProcessImagePath(process.Id);
                if (image is not null && PathGuard.IsStrictlyUnder(image, root))
                {
                    result.Add(new FoundProcess(process.Id, image));
                }
            }
        }

        return result;
    }

    public bool TryKill(FoundProcess process, string folder)
    {
        ArgumentNullException.ThrowIfNull(process);
        var image = Win32.TryGetProcessImagePath(process.ProcessId);
        if (image is null)
        {
            return !IsAlive(process.ProcessId); // gone already counts as success; alive but unqueryable does not
        }

        if (!string.Equals(image, process.ImagePath, StringComparison.OrdinalIgnoreCase) || !PathGuard.IsStrictlyUnder(image, Path.GetFullPath(folder)))
        {
            return true; // the PID now belongs to an unrelated process: the original one is gone
        }

        try
        {
            using var p = Process.GetProcessById(process.ProcessId);
            // Only this process: descendants whose images live elsewhere (an editor or shell started from a terminal in
            // the folder) are the user's other work. In-folder descendants are found and stopped on their own.
            p.Kill(entireProcessTree: false);
            return p.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException)
        {
            return true; // exited meanwhile
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return true;
        }
    }
}
