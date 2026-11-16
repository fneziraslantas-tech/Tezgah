using Tezgah.Core.Execution;

namespace Tezgah.Core.Tests.Execution;

public class CmdArgumentGuardTests
{
    private const string Script = @"C:\tools\node\npm.cmd";

    [Theory]
    [InlineData("foo & calc")]
    [InlineData("foo&calc")]
    [InlineData("\"a\" | b")]
    [InlineData("a|b")]
    [InlineData("%PATH%")]
    [InlineData("100%")]
    [InlineData("!x!")]
    [InlineData("^")]
    [InlineData("a^b")]
    [InlineData("line1\nline2")]
    [InlineData("line1\r\ncalc")]
    [InlineData("a\"b")]
    [InlineData("\"")]
    [InlineData("a > out.txt")]
    [InlineData("<in")]
    [InlineData("(calc)")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tab\there")]
    [InlineData("nul\0byte")]
    [InlineData("ｆｕｌｌ＂ｗｉｄｔｈ")] // U+FF02 best-fits to '"' in ANSI code pages (WorstFit)
    public void Malicious_arguments_are_refused(string argument)
    {
        Assert.False(CmdArgumentGuard.IsSafeArgument(argument));
        Assert.Throws<UnsafeCommandArgumentException>(() => CmdArgumentGuard.BuildCmdArguments(Script, ["run", argument]));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("dev")]
    [InlineData("--port=3000")]
    [InlineData("-p")]
    [InlineData("@scope/pkg@1.2.3")]
    [InlineData("C:\\src\\app")]
    [InlineData("./src/index.js")]
    [InlineData("key=value")]
    [InlineData("a,b")]
    [InlineData("C:\\My Projects\\app")]
    [InlineData("çalıştır")]
    public void Safe_arguments_are_accepted(string argument) => Assert.True(CmdArgumentGuard.IsSafeArgument(argument));

    [Fact]
    public void Builds_quoted_command_line()
    {
        var args = CmdArgumentGuard.BuildCmdArguments(@"C:\Program Files\nodejs\npm.cmd", ["run", "dev", "C:\\My Projects\\app", "--port=3000"]);
        Assert.Equal("/d /v:off /s /c \"\"C:\\Program Files\\nodejs\\npm.cmd\" run dev \"C:\\My Projects\\app\" \"--port=3000\"\"", args);
    }

    [Theory]
    [InlineData(@"C:\tools\%evil%\npm.cmd")]
    [InlineData(@"C:\tools\a&b\npm.cmd")]
    [InlineData(@"C:\tools\a!b!\npm.cmd")]
    [InlineData(@"npm.cmd")]
    [InlineData(@"C:\tools\npm.exe")]
    public void Unsafe_script_paths_are_refused(string script) =>
        Assert.Throws<UnsafeCommandArgumentException>(() => CmdArgumentGuard.BuildCmdArguments(script, ["run"]));

    [Fact]
    public void Cmd_is_taken_from_system_directory() =>
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), CmdArgumentGuard.CmdPath);
}
