using Tezgah.Core.Execution;

namespace Tezgah.Core.Tests.Execution;

public class OutputTextTests
{
    [Theory]
    [InlineData("\u001b[32mVITE\u001b[39m ready", "VITE ready")]
    [InlineData("\u001b[1;31merror\u001b[0m: x", "error: x")]
    [InlineData("\u001b]0;title\u0007text", "text")]
    [InlineData("\u001b]8;;http://x\u001b\\link\u001b]8;;\u001b\\", "link")]
    [InlineData("\u001b[2K\u001b[1Gprogress", "progress")]
    [InlineData("plain", "plain")]
    [InlineData("tab\tkept\r", "tab\tkept")]
    public void Strips_ansi(string input, string expected) => Assert.Equal(expected, OutputText.StripAnsi(input));

    [Theory]
    [InlineData("  ➜  Local:   http://localhost:5173/", "http://localhost:5173/")]
    [InlineData("Listening on http://0.0.0.0:8000.", "http://localhost:8000/")]
    [InlineData("Running on http://127.0.0.1:5000 (Press CTRL+C to quit)", "http://127.0.0.1:5000/")]
    [InlineData("see https://[::1]:7001/swagger,", "https://[::1]:7001/swagger")]
    [InlineData("open http://LOCALHOST:3000/app?x=1", "http://localhost:3000/app?x=1")]
    public void Detects_local_urls(string line, string expected)
    {
        var uri = Assert.Single(OutputText.FindLocalUrls(line));
        Assert.Equal(expected, uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("visit https://github.com/x/y")]
    [InlineData("http://localhost.evil.com/")]
    [InlineData("ftp://localhost:21")]
    [InlineData("no url here")]
    [InlineData("http://127.0.0.1.nip.io/")]
    public void Ignores_non_local_urls(string line) => Assert.Empty(OutputText.FindLocalUrls(line));

    [Fact]
    public void Ring_buffer_keeps_last_items_in_order()
    {
        var buffer = new RingBuffer<int>(3);
        for (var i = 1; i <= 5; i++)
        {
            buffer.Add(i);
        }

        Assert.Equal([3, 4, 5], buffer.Snapshot());
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void Ring_buffer_partial_fill()
    {
        var buffer = new RingBuffer<string>(5000);
        buffer.Add("a");
        buffer.Add("b");
        Assert.Equal(["a", "b"], buffer.Snapshot());
    }
}
