using Tezgah.App.Services;

namespace Tezgah.App.Tests;

public class ShellServiceTests
{
    [Theory]
    [InlineData(@"C:\src\tool")]
    [InlineData(@"C:\src\my tool (v2)")]
    public void Windows_terminal_never_receives_the_path_on_its_command_line(string folder)
    {
        var psi = ShellService.BuildTerminalStartInfo(@"C:\wt.exe", @"C:\pwsh.exe", folder)!;
        Assert.Equal(@"C:\wt.exe", psi.FileName);
        Assert.Equal(["-d", "."], psi.ArgumentList);
        Assert.Equal(folder, psi.WorkingDirectory);
    }

    [Theory]
    [InlineData(@"C:\src\tool;powershell -nop -e ZQBjAGgAbwA=")]
    [InlineData(@"C:\src\;calc")]
    [InlineData(@"C:\src\%PATH%")]
    public void Paths_with_terminal_syntax_use_powershell_with_only_a_working_directory(string folder)
    {
        var psi = ShellService.BuildTerminalStartInfo(@"C:\wt.exe", @"C:\pwsh.exe", folder)!;
        Assert.Equal(@"C:\pwsh.exe", psi.FileName);
        Assert.Equal(["-NoLogo"], psi.ArgumentList);
        Assert.Equal(folder, psi.WorkingDirectory);
        Assert.DoesNotContain(psi.ArgumentList, a => a.Contains(';', StringComparison.Ordinal));
    }
}
