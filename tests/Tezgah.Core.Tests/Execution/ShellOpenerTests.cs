using Tezgah.Core.Execution;

namespace Tezgah.Core.Tests.Execution;

public class ShellOpenerTests
{
    [Theory]
    [InlineData("https://github.com/user/repo")]
    [InlineData("http://localhost:5173/")]
    public void Accepts_http_urls(string url) => Assert.True(ShellOpener.IsSafeUrl(url, out _));

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("calc")]
    [InlineData("ms-settings:privacy")]
    [InlineData("search-ms:query=x&crumb=location:\\\\evil\\share")]
    [InlineData("javascript:alert(1)")]
    [InlineData("\\\\evil\\share\\x.exe")]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("https://example.com/\nx")]
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_everything_else(string? url)
    {
        Assert.False(ShellOpener.IsSafeUrl(url, out _));
        Assert.False(ShellOpener.OpenUrl(url, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("\\\\server\\share")]
    [InlineData("relative\\folder")]
    [InlineData("C:\\definitely\\missing\\folder-xyz")]
    public void OpenFolder_refuses_invalid_folders(string path)
    {
        Assert.False(ShellOpener.OpenFolder(path, out var error));
        Assert.NotNull(error);
    }
}
