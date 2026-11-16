using Tezgah.Core.Discovery;
using Tezgah.Core.Execution;

namespace Tezgah.Core.Tests.Execution;

/// <summary>Batch shims run by Tezgah's tool commands must not pick up programs planted in the working directory.</summary>
public class CurrentDirectoryLookupTests
{
    private static (string Shim, string Repo) Setup(TempDir dir)
    {
        // The variable may be inherited from the developer's shell; the protection must come from Tezgah itself.
        Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", null);

        var tools = dir.Sub("tools");
        var shim = Path.Combine(tools, "shim.cmd");
        File.WriteAllText(shim, "@echo off\r\ntezgahplanted\r\necho SHIM-DONE\r\n");
        var repo = dir.Sub("repo");
        File.WriteAllText(Path.Combine(repo, "tezgahplanted.cmd"), "@echo off\r\necho PLANTED-RAN\r\n");
        return (shim, repo);
    }

    [Fact]
    public async Task Command_executor_shims_do_not_run_programs_from_the_working_directory()
    {
        using var dir = new TempDir();
        var (shim, repo) = Setup(dir);

        var result = await new CommandExecutor().RunAsync(Specs.Command(shim, repo), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Contains("SHIM-DONE", result.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("PLANTED-RAN", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_cli_shims_do_not_run_programs_from_the_working_directory()
    {
        using var dir = new TempDir();
        var (shim, repo) = Setup(dir);

        var result = await Cli.RunAsync(shim, [], repo, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Contains("SHIM-DONE", result.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("PLANTED-RAN", result.StdOut, StringComparison.Ordinal);
    }
}
