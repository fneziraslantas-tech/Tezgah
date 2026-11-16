using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Discovery;

namespace Tezgah.Core.Tests.Discovery;

public class GitStatusParserTests
{
    [Fact]
    public void ParsesBranchUpstreamAheadBehindAndEntries()
    {
        const string output =
            "# branch.oid 1234567890abcdef\n" +
            "# branch.head feature/x\n" +
            "# branch.upstream origin/feature/x\n" +
            "# branch.ab +3 -2\n" +
            "# stash 4\n" +
            "1 .M N... 100644 100644 100644 abc abc src/a.cs\n" +
            "1 A. N... 000000 100644 100644 000 abc new file.txt\n" +
            "2 R. N... 100644 100644 100644 abc abc R100 b.cs\ta.cs\n" +
            "u UU N... 100644 100644 100644 100644 a b c conflict.cs\n" +
            "? untracked1.txt\n" +
            "? dir with space/\n" +
            "! ignored.log\n";
        var s = GitStatusParser.Parse(output);
        Assert.Equal("feature/x", s.Branch);
        Assert.Equal("origin/feature/x", s.Upstream);
        Assert.Equal(3, s.Ahead);
        Assert.Equal(2, s.Behind);
        Assert.Equal(4, s.Modified);
        Assert.Equal(2, s.Untracked);
        Assert.Equal(4, s.Stashes);
        Assert.False(s.IsInitialCommit);
    }

    [Fact]
    public void DetachedInitialAndCrlf()
    {
        var s = GitStatusParser.Parse("# branch.oid (initial)\r\n# branch.head (detached)\r\n");
        Assert.Null(s.Branch);
        Assert.Null(s.Upstream);
        Assert.True(s.IsInitialCommit);
        Assert.Null(s.Stashes);
        Assert.Equal(0, s.Modified);
    }

    [Fact]
    public void GarbageIsIgnored()
    {
        var s = GitStatusParser.Parse("fatal: something\n# branch.ab +x -y\n1\n?\n");
        Assert.Equal(0, s.Ahead);
        Assert.Equal(0, s.Modified);
        Assert.Equal(0, s.Untracked);
    }

    [Fact]
    public void ParsesLogLine()
    {
        var (at, subject) = GitClient.ParseLog("2026-01-02T03:04:05+03:00\0Fix: things\n");
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(3)), at);
        Assert.Equal("Fix: things", subject);
    }

    [Fact]
    public void DetectsDubiousOwnership() =>
        Assert.True(GitClient.IsDubiousOwnership("fatal: detected dubious ownership in repository at 'C:/x'"));
}

public class GitUrlsTests
{
    [Theory]
    [InlineData("git@github.com:owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("git@github.com:owner/repo", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/", "https://github.com/owner/repo")]
    [InlineData("ssh://git@github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("ssh://git@gitlab.example.com:2222/group/sub/repo.git", "https://gitlab.example.com/group/sub/repo")]
    [InlineData("https://user:ghp_secret@github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData(@"C:\repos\local", null)]
    [InlineData("../other", null)]
    [InlineData(null, null)]
    public void ToWebUrl(string? remote, string? expected) => Assert.Equal(expected, GitUrls.ToWebUrl(remote));

    [Fact]
    public void SanitizeStripsCredentials()
    {
        var s = GitUrls.Sanitize("https://user:ghp_secret@github.com/owner/repo.git")!;
        Assert.DoesNotContain("ghp_secret", s, StringComparison.Ordinal);
        Assert.DoesNotContain("user", s, StringComparison.Ordinal);
        Assert.Equal("git@github.com:o/r.git", GitUrls.Sanitize("git@github.com:o/r.git"));
    }
}

public sealed class GitClientTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task ReadsRealRepository()
    {
        if (!TestTools.GitInitWithCommit(_dir.Path))
        {
            return; // git not installed: skip
        }

        File.WriteAllText(Path.Combine(_dir.Path, "README.md"), "changed");
        File.WriteAllText(Path.Combine(_dir.Path, "new.txt"), "x");
        TestTools.Run("git", _dir.Path, "remote", "add", "origin", "https://github.com/example/demo.git");

        var git = new GitClient(NullLogger<GitClient>.Instance);
        var info = await git.GetInfoAsync(_dir.Path, fetch: false, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal("main", info!.Branch);
        Assert.Equal(1, info.ModifiedFiles);
        Assert.Equal(1, info.UntrackedFiles);
        Assert.Equal(0, info.Stashes);
        Assert.False(info.HasUpstream);
        Assert.Equal("initial commit", info.LastCommitSubject);
        Assert.NotNull(info.LastCommitAt);
        Assert.Equal("https://github.com/example/demo.git", info.RemoteUrl);
        Assert.True(info.HasUnsavedWork);
    }

    [Fact]
    public async Task NonRepositoryReturnsNull()
    {
        var git = new GitClient(NullLogger<GitClient>.Instance);
        if (!await git.IsAvailableAsync(CancellationToken.None))
        {
            return;
        }

        // A temp dir is not a repository (assuming %TEMP% itself is not inside one).
        Assert.Null(await git.GetInfoAsync(_dir.Path, fetch: false, CancellationToken.None));
    }
}
