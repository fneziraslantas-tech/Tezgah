using System.Diagnostics;
using Tezgah.Core.Execution;

namespace Tezgah.Core.Tests.Execution;

public class ProcessHostTests
{
    private readonly ProcessHost _host = new();

    [Fact]
    public async Task Captures_output_detects_url_and_exit_code()
    {
        using var dir = new TempDir();
        var profile = Specs.Profile("p", Specs.Command("cmd.exe", dir.Path, "/d", "/c", "echo Local: http://0.0.0.0:5173/ ready&& echo %PYTHONUNBUFFERED%-%PYTHONIOENCODING%&& exit 3"));
        await using var run = (RunningProcess)_host.Start("item", profile);
        var urls = new List<Uri>();
        run.UrlDetected += (_, u) => urls.Add(u);

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(await Specs.WaitUntilAsync(() => run.Output.Count >= 2, TimeSpan.FromSeconds(5)));
        Assert.True(run.HasExited);
        Assert.Equal(3, run.ExitCode);
        Assert.Contains(run.Output, l => l.Text.Contains("1-utf-8", StringComparison.Ordinal));
        var url = Assert.Single(run.DetectedUrls);
        Assert.Equal("http://localhost:5173/", url.AbsoluteUri);
        Assert.NotEqual(0, run.ProcessId);
    }

    [Fact]
    public async Task Stop_kills_grandchildren_through_the_job()
    {
        using var dir = new TempDir();
        var before = PingPids();

        // cmd (child) → ping (grandchild). Stopping must take down ping too.
        var profile = Specs.Profile("p", Specs.Command(Specs.Cmd, dir.Path, "/d", "/c", "ping -n 60 127.0.0.1 > nul"));
        await using var run = (RunningProcess)_host.Start("item", profile);

        int? grandchild = null;
        Assert.True(await Specs.WaitUntilAsync(
            () =>
            {
                grandchild = PingPids().Except(before).Cast<int?>().FirstOrDefault();
                return grandchild is not null;
            },
            TimeSpan.FromSeconds(15)), "ping did not start");

        await run.StopAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.True(run.HasExited);
        Assert.True(await Specs.WaitUntilAsync(() => !IsAlive(grandchild!.Value), TimeSpan.FromSeconds(10)), "grandchild ping survived");
    }

    [Fact]
    public async Task Dispose_kills_the_tree()
    {
        using var dir = new TempDir();
        var profile = Specs.Profile("p", Specs.Command("ping.exe", dir.Path, "-n", "60", "127.0.0.1"));
        var run = (RunningProcess)_host.Start("item", profile);
        var pid = run.ProcessId;
        await run.DisposeAsync();
        Assert.True(await Specs.WaitUntilAsync(() => !IsAlive(pid), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Stdin_is_forwarded_and_closing_it_stops_gracefully()
    {
        using var dir = new TempDir();
        var profile = Specs.Profile("p", Specs.Command("more.com", dir.Path));
        await using var run = (RunningProcess)_host.Start("item", profile);
        await run.SendInputAsync("tezgah-stdin-line", CancellationToken.None);
        await run.StopAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(await Specs.WaitUntilAsync(() => run.Output.Any(l => l.Text.Contains("tezgah-stdin-line", StringComparison.Ordinal)), TimeSpan.FromSeconds(5)));
        Assert.True(run.HasExited);
    }

    [Fact]
    public async Task Output_buffer_is_bounded()
    {
        using var dir = new TempDir();
        var psi = ProcessLaunch.CreateStartInfo(Specs.Cmd, Specs.Command(Specs.Cmd, dir.Path, "/d", "/c", "for /L %i in (1,1,300) do @echo line%i"), redirectInput: true);
        await using var run = RunningProcess.Start("item", Specs.Profile("p", Specs.Command(Specs.Cmd, dir.Path)), psi, bufferLines: 100);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(await Specs.WaitUntilAsync(() => run.Output.Count == 100 && run.Output[^1].Text == "line300", TimeSpan.FromSeconds(5)));
        Assert.Equal("line201", run.Output[0].Text);
    }

    [Fact]
    public void Missing_working_directory_throws()
    {
        var profile = Specs.Profile("p", Specs.Command("cmd.exe", Path.Combine(Path.GetTempPath(), "TezgahTests", "missing-" + Guid.NewGuid())));
        Assert.Throws<DirectoryNotFoundException>(() => _host.Start("item", profile));
    }

    private static HashSet<int> PingPids()
    {
        var result = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("PING"))
        {
            using (p)
            {
                result.Add(p.Id);
            }
        }

        return result;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
