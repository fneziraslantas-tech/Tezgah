using System.Diagnostics;
using Tezgah.Core.Execution;

namespace Tezgah.Core.Tests.Execution;

public class ProcessFinderTests
{
    [Fact]
    public async Task Stopping_a_folder_process_spares_children_whose_image_lives_elsewhere()
    {
        using var dir = new TempDir();
        var app = dir.Sub("tools", "app");
        var shell = Path.Combine(app, "shell.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), shell);

        var psi = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
        using var parent = Process.Start(psi)!;
        Process? child = null;
        try
        {
            Assert.True(await Specs.WaitUntilAsync(() => (child = FindChild(parent.Id, "PING.EXE")) is not null, TimeSpan.FromSeconds(10)));

            var finder = new ProcessFinder();
            var found = Assert.Single(finder.FindUnder(app));
            Assert.True(finder.TryKill(found, app));

            Assert.True(parent.WaitForExit(5000));
            Assert.False(child!.HasExited, "the user's other program (image outside the folder) was killed with the tree");
        }
        finally
        {
            try
            {
                child?.Kill();
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                parent.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            child?.Dispose();
        }
    }

    private static Process? FindChild(int parentId, string imageName)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(imageName)))
        {
            if (ParentOf(p.Id) == parentId)
            {
                return p;
            }

            p.Dispose();
        }

        return null;
    }

    private static int? ParentOf(int pid)
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(cmd) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add($"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').ParentProcessId");
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return int.TryParse(output.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parent) ? parent : null;
    }
}
