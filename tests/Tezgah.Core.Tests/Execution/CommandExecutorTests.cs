using System.Diagnostics;
using Tezgah.Core.Execution;

namespace Tezgah.Core.Tests.Execution;

public class CommandExecutorTests
{
    private readonly CommandExecutor _executor = new();

    [Fact]
    public async Task Runs_and_captures_output()
    {
        using var dir = new TempDir();
        var result = await _executor.RunAsync(Specs.Command("cmd.exe", dir.Path, "/d", "/c", "echo merhaba-ğüş"), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Contains("merhaba", result.StdOut, StringComparison.Ordinal);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Non_zero_exit_code_is_reported()
    {
        using var dir = new TempDir();
        var result = await _executor.RunAsync(Specs.Command("cmd.exe", dir.Path, "/d", "/c", "exit 7"), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Timeout_kills_the_tree()
    {
        using var dir = new TempDir();
        var sw = Stopwatch.StartNew();
        var result = await _executor.RunAsync(Specs.Command("ping.exe", dir.Path, "-n", "30", "127.0.0.1"), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(result.TimedOut);
        Assert.False(result.Success);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Environment_is_merged()
    {
        using var dir = new TempDir();
        var spec = Specs.Command("cmd.exe", dir.Path, "/d", "/c", "echo %TEZGAH_TEST_VAR%") with
        {
            Environment = new Dictionary<string, string> { ["TEZGAH_TEST_VAR"] = "value42" },
        };
        var result = await _executor.RunAsync(spec, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Contains("value42", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_file_receives_safe_arguments_intact()
    {
        using var dir = new TempDir();
        var script = dir.File("show.cmd", "@echo off\r\necho ARGS:%*\r\necho ONE:%~1\r\n");
        var result = await _executor.RunAsync(Specs.Command(script, dir.Path, "hello", "a b", "--port=3000"), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(result.Success, result.StdErr);
        Assert.Contains("ARGS:hello \"a b\" \"--port=3000\"", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("ONE:hello", result.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x & echo INJECTED")]
    [InlineData("%PATH%")]
    [InlineData("a\"b")]
    public async Task Batch_file_with_malicious_argument_is_refused_before_start(string argument)
    {
        using var dir = new TempDir();
        var marker = Path.Combine(dir.Path, "ran.txt");
        var script = dir.File("touch.cmd", $"@echo off\r\necho ran> \"{marker}\"\r\n");
        await Assert.ThrowsAsync<UnsafeCommandArgumentException>(() =>
            _executor.RunAsync(Specs.Command(script, dir.Path, argument), TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Relative_executable_resolves_against_working_directory()
    {
        using var dir = new TempDir();
        dir.File(@"scripts\hi.cmd", "@echo off\r\necho HI-THERE\r\n");
        var result = await _executor.RunAsync(Specs.Command(@"scripts\hi", dir.Path), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Contains("HI-THERE", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolves_from_path_with_pathext()
    {
        var resolved = _executor.ResolveExecutable("cmd");
        Assert.NotNull(resolved);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), resolved, ignoreCase: true);
        Assert.Null(_executor.ResolveExecutable("definitely-not-a-real-tool-xyz"));
        Assert.Null(_executor.ResolveExecutable("bad\"name"));
    }

    [Fact]
    public void Bare_names_are_not_resolved_from_the_working_directory()
    {
        using var dir = new TempDir();
        dir.File("tezgah-planted-tool.cmd", "@echo off\r\n");
        Assert.Null(ProcessLaunch.Resolve("tezgah-planted-tool", dir.Path));
    }

    [Fact]
    public void Non_executable_extensions_are_not_resolved()
    {
        using var dir = new TempDir();
        var js = dir.File("tool.js", "");
        Assert.Null(ProcessLaunch.Resolve(js, dir.Path));
    }

    [Fact]
    public async Task Missing_executable_throws()
    {
        using var dir = new TempDir();
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _executor.RunAsync(Specs.Command("no-such-tool-xyz", dir.Path), TimeSpan.FromSeconds(5), CancellationToken.None));
    }
}
