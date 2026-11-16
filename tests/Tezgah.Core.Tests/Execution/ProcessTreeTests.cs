using System.Diagnostics;
using Tezgah.Core.Discovery;
using Tezgah.Core.Execution;

namespace Tezgah.Core.Tests.Execution;

/// <summary>Self-relaunching commands (NSIS/Inno uninstallers) and orphaned pipe holders.</summary>
public class ProcessTreeTests
{
    /// <summary>launcher.cmd starts worker.cmd in the background (same job, inherits the pipes) and exits at once.</summary>
    private static (string Launcher, string Marker) WriteLauncher(TempDir dir, int pingCount)
    {
        var marker = Path.Combine(dir.Path, "marker.txt");
        dir.File("worker.cmd", $"@echo off\r\nping -n {pingCount} 127.0.0.1 >nul\r\necho done> \"%~dp0marker.txt\"\r\n");
        var launcher = dir.File("launcher.cmd", "@echo off\r\nstart \"\" /b \"%~dp0worker.cmd\"\r\nexit /b 0\r\n");
        return (launcher, marker);
    }

    [Fact]
    public async Task Tree_mode_waits_for_relaunched_children()
    {
        using var dir = new TempDir();
        var (launcher, marker) = WriteLauncher(dir, 3);
        var spec = Specs.Command(launcher, dir.Path) with { WaitForProcessTree = true };

        var result = await new CommandExecutor().RunAsync(spec, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.True(result.Success, result.StdErr);
        Assert.True(File.Exists(marker), "the relaunched child was killed before it finished");
    }

    [Fact]
    public async Task Default_mode_still_kills_leftovers_when_the_root_exits()
    {
        using var dir = new TempDir();
        var (launcher, marker) = WriteLauncher(dir, 3);

        var result = await new CommandExecutor().RunAsync(Specs.Command(launcher, dir.Path), TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.True(result.Success, result.StdErr);
        await Task.Delay(4000);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Tree_mode_times_out_and_kills_the_tree()
    {
        using var dir = new TempDir();
        var (launcher, marker) = WriteLauncher(dir, 30);
        var spec = Specs.Command(launcher, dir.Path) with { WaitForProcessTree = true };

        var result = await new CommandExecutor().RunAsync(spec, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Tree_mode_stops_waiting_for_unrelated_survivors_and_never_kills_them()
    {
        using var dir = new TempDir();

        // The "uninstaller" opens something long-lived that is not part of the uninstall (a browser survey page, a tray
        // helper...) and exits. Its image (System32\ping.exe here) is neither the uninstaller, a temp copy nor an engine.
        var launcher = dir.File("uninst-launcher.cmd", "@echo off\r\nstart \"\" /b ping -n 40 127.0.0.2 >nul\r\nexit /b 0\r\n");
        var spec = Specs.Command(launcher, dir.Path) with { WaitForProcessTree = true };
        var before = Process.GetProcessesByName("ping").Select(p => p.Id).ToHashSet();

        var sw = Stopwatch.StartNew();
        var result = await new CommandExecutor().RunAsync(spec, TimeSpan.FromSeconds(60), CancellationToken.None);
        sw.Stop();

        var survivors = Process.GetProcessesByName("ping").Where(p => !before.Contains(p.Id)).ToList();
        try
        {
            Assert.True(result.Success, result.StdErr);
            Assert.False(result.TimedOut);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"waited {sw.Elapsed} for a process that is not the uninstaller");
            Assert.NotEmpty(survivors); // left running, not killed when the job closed
        }
        finally
        {
            foreach (var p in survivors)
            {
                try
                {
                    p.Kill();
                }
                catch (InvalidOperationException)
                {
                }

                p.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\msiexec.exe", true)]
    [InlineData(@"C:\Users\u\AppData\Local\Temp\~nsuA.tmp\Au_.exe", true)]
    [InlineData(@"C:\Users\u\AppData\Local\Temp\is-ABC.tmp\_iu14D2N.tmp", true)]
    [InlineData(@"C:\Program Files\Vendor\unins000.exe", true)]
    [InlineData(@"C:\Program Files\Vendor\helper.exe", true)] // same folder as the uninstaller
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", false)]
    [InlineData(@"C:\Windows\explorer.exe", false)]
    [InlineData(null, true)]
    public void Installer_engines_are_recognised(string? image, bool engine)
    {
        var tempImage = image?.Replace(@"C:\Users\u\AppData\Local\Temp\", Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(engine, CommandExecutor.IsInstallerEngine(tempImage, @"C:\Program Files\Vendor\uninstall-me.exe"));
    }

    [Fact]
    public async Task Discovery_cli_returns_when_the_root_exits_even_if_a_child_holds_the_pipe()
    {
        using var dir = new TempDir();
        var (launcher, marker) = WriteLauncher(dir, 20);
        var sw = Stopwatch.StartNew();

        var result = await Cli.RunAsync(launcher, [], dir.Path, TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        await Task.Delay(500);
        Assert.False(File.Exists(marker)); // the orphan was killed with the job
    }
}
