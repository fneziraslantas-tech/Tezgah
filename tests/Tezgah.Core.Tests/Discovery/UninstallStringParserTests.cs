using Tezgah.Core.Discovery;

namespace Tezgah.Core.Tests.Discovery;

public class UninstallStringParserTests
{
    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Program Files\Foo App\unins000.exe",
        @"C:\Program Files (x86)\Bar\uninst.exe",
        @"C:\Tools\x.exe",
        @"C:\Program Files\NoExt\remove.exe",
    };

    private static ParsedCommandLine Parse(string s) => UninstallStringParser.Parse(s, Files.Contains)!;

    [Fact]
    public void QuotedPathWithArgs()
    {
        var p = Parse("\"C:\\Program Files\\Foo App\\unins000.exe\" /SILENT");
        Assert.Equal(@"C:\Program Files\Foo App\unins000.exe", p.FileName);
        Assert.Equal(["/SILENT"], p.Arguments);
    }

    [Fact]
    public void QuotedPathWithoutArgs()
    {
        var p = Parse("\"C:\\Program Files\\Foo App\\unins000.exe\"");
        Assert.Equal(@"C:\Program Files\Foo App\unins000.exe", p.FileName);
        Assert.Empty(p.Arguments);
    }

    [Fact]
    public void UnquotedPathWithSpacesIsProbed()
    {
        var p = Parse(@"C:\Program Files\Foo App\unins000.exe /SILENT /NORESTART");
        Assert.Equal(@"C:\Program Files\Foo App\unins000.exe", p.FileName);
        Assert.Equal(["/SILENT", "/NORESTART"], p.Arguments);
    }

    [Fact]
    public void UnquotedPathWithParenthesesIsProbed()
    {
        var p = Parse(@"C:\Program Files (x86)\Bar\uninst.exe /S");
        Assert.Equal(@"C:\Program Files (x86)\Bar\uninst.exe", p.FileName);
        Assert.Equal(["/S"], p.Arguments);
    }

    [Fact]
    public void UnquotedPathWithoutSpaces()
    {
        var p = Parse(@"C:\Tools\x.exe --uninstall");
        Assert.Equal(@"C:\Tools\x.exe", p.FileName);
        Assert.Equal(["--uninstall"], p.Arguments);
    }

    [Fact]
    public void UnquotedPathWithImpliedExe()
    {
        var p = Parse(@"C:\Program Files\NoExt\remove /quiet");
        Assert.Equal(@"C:\Program Files\NoExt\remove.exe", p.FileName);
        Assert.Equal(["/quiet"], p.Arguments);
    }

    [Fact]
    public void UnquotedMissingFileFallsBackToExtension()
    {
        var p = Parse(@"C:\Program Files\Gone\uninstall.exe /x");
        Assert.Equal(@"C:\Program Files\Gone\uninstall.exe", p.FileName);
        Assert.Equal(["/x"], p.Arguments);
    }

    [Fact]
    public void MsiExecInstallSwitchBecomesRemove()
    {
        var p = Parse("MsiExec.exe /I{12345678-1234-1234-1234-123456789ABC}");
        Assert.Equal("MsiExec.exe", p.FileName);
        Assert.Equal(["/X{12345678-1234-1234-1234-123456789ABC}"], p.Arguments);
    }

    [Fact]
    public void MsiExecRemoveSwitchKept()
    {
        var p = Parse("MsiExec.exe /X{12345678-1234-1234-1234-123456789ABC} /qn");
        Assert.Equal(["/X{12345678-1234-1234-1234-123456789ABC}", "/qn"], p.Arguments);
    }

    [Fact]
    public void MsiExecSeparatedInstallSwitch()
    {
        var p = Parse("msiexec /i {12345678-1234-1234-1234-123456789ABC}");
        Assert.Equal(["/X", "{12345678-1234-1234-1234-123456789ABC}"], p.Arguments);
    }

    [Fact]
    public void MsiExecLowercaseFullPath()
    {
        var p = Parse(@"C:\Windows\System32\msiexec.exe /I{AAAAAAAA-1234-1234-1234-123456789ABC}");
        Assert.Equal(@"C:\Windows\System32\msiexec.exe", p.FileName);
        Assert.Equal(["/X{AAAAAAAA-1234-1234-1234-123456789ABC}"], p.Arguments);
    }

    [Fact]
    public void QuotedArgumentWithSpaces()
    {
        var p = Parse("\"C:\\Tools\\x.exe\" --uninstall \"C:\\Program Files\\Foo\" -s");
        Assert.Equal(["--uninstall", @"C:\Program Files\Foo", "-s"], p.Arguments);
    }

    [Fact]
    public void EscapedQuotesFollowArgvRules()
    {
        var p = Parse("\"C:\\Tools\\x.exe\" a\\\"b \"c\"\"d\" e\\\\\"f g\"");
        Assert.Equal(["a\"b", "c\"d", "e\\f g"], p.Arguments);
    }

    [Fact]
    public void TrailingBackslashesNotBeforeQuoteAreLiteral()
    {
        var p = Parse("\"C:\\Tools\\x.exe\" C:\\dir\\ next");
        Assert.Equal([@"C:\dir\", "next"], p.Arguments);
    }

    [Fact]
    public void RundllStyle()
    {
        var p = Parse("rundll32.exe dfshim.dll,ShArpMaintain App.application, Culture=neutral");
        Assert.Equal("rundll32.exe", p.FileName);
        Assert.Equal(["dfshim.dll,ShArpMaintain", "App.application,", "Culture=neutral"], p.Arguments);
    }

    [Fact]
    public void SquirrelUpdateExe()
    {
        var p = Parse("\"C:\\Users\\u\\AppData\\Local\\Discord\\Update.exe\" --uninstall -s");
        Assert.Equal(@"C:\Users\u\AppData\Local\Discord\Update.exe", p.FileName);
        Assert.Equal(["--uninstall", "-s"], p.Arguments);
    }

    [Fact]
    public void EnvironmentVariablesExpanded()
    {
        var p = UninstallStringParser.Parse("\"%SystemRoot%\\System32\\x.exe\" /u", _ => false)!;
        Assert.DoesNotContain("%", p.FileName, StringComparison.Ordinal);
        Assert.EndsWith(@"\System32\x.exe", p.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyReturnsNull(string? s) => Assert.Null(UninstallStringParser.Parse(s, _ => true));

    [Fact]
    public void LeadingWhitespaceIgnored()
    {
        var p = Parse("   \"C:\\Tools\\x.exe\"   /a   /b  ");
        Assert.Equal(@"C:\Tools\x.exe", p.FileName);
        Assert.Equal(["/a", "/b"], p.Arguments);
    }

    [Fact]
    public void UnknownBareCommandSplitsAtFirstSpace()
    {
        var p = Parse("someuninstaller /quiet");
        Assert.Equal("someuninstaller", p.FileName);
        Assert.Equal(["/quiet"], p.Arguments);
    }
}
