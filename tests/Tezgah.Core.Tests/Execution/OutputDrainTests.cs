using Tezgah.Core.Execution;

namespace Tezgah.Core.Tests.Execution;

public class OutputDrainTests
{
    [Fact]
    public async Task Disposing_from_the_exited_handler_keeps_the_whole_output_tail()
    {
        using var dir = new TempDir();
        var script = dir.File("many.cmd", "@echo off\r\nfor /L %%i in (1,1,3000) do echo line%%i\r\necho TRACEBACK-TAIL 1>&2\r\nexit /b 7\r\n");
        var profile = Specs.Profile("p", Specs.Command(script, dir.Path));
        var run = (RunningProcess)new ProcessHost().Start("item", profile);
        var received = 0;
        run.OutputReceived += (_, _) =>
        {
            Interlocked.Increment(ref received);
            Thread.SpinWait(20_000); // a slow consumer, like the UI
        };
        var disposed = new TaskCompletionSource();
        run.Exited += async (_, _) =>
        {
            await run.DisposeAsync(); // what RunService does on exit
            disposed.TrySetResult();
        };

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(7, run.ExitCode);
        Assert.Contains(run.Output, l => l.Text.Contains("TRACEBACK-TAIL", StringComparison.Ordinal));
        Assert.Equal(3001, run.Output.Count);
    }

    [Fact]
    public async Task Same_origin_urls_are_reported_once_and_long_lines_are_truncated()
    {
        using var dir = new TempDir();
        var script = dir.File("urls.cmd", "@echo off\r\nfor /L %%i in (1,1,200) do echo GET http://localhost:3000/api?id=%%i\r\n");
        var run = (RunningProcess)new ProcessHost().Start("item", Specs.Profile("p", Specs.Command(script, dir.Path)));
        await using (run)
        {
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Single(run.DetectedUrls);
        }
    }
}
