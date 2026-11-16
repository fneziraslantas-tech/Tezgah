using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Tezgah.Core.Discovery;

namespace Tezgah.Core.Tests.Discovery;

/// <summary>
/// The managed git reader's commit walks: correct with hand-written loose objects (no git needed) and cheap in memory
/// (reviewer PoC: a fixed 256 KB buffer per inflated commit made one Read() of a diverged repository allocate ~94 MB).
/// </summary>
public sealed class GitMetadataReaderCostTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>A repository with HEAD on main tracking origin/main; objects are written loose by <see cref="Commit"/>.</summary>
    private string NewRepo(string name, out string gitDir)
    {
        var repo = _dir.Sub(name);
        gitDir = _dir.Sub(name, ".git");
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(gitDir, "config"),
            "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = https://example.com/x.git\n[branch \"main\"]\n\tremote = origin\n\tmerge = refs/heads/main\n");
        return repo;
    }

    private static void SetRef(string gitDir, string name, string id)
    {
        var path = Path.Combine(gitDir, name.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, id + "\n");
    }

    private static string Commit(string gitDir, string? parent, string message, long time = 1_700_000_000, string extraHeaders = "")
    {
        var body = new StringBuilder("tree 4b825dc642cb6eb9a060e54bf8d69288fbee4904\n");
        if (parent is not null)
        {
            body.Append("parent ").Append(parent).Append('\n');
        }

        body.Append("author T <t@e.com> ").Append(time).Append(" +0000\n");
        body.Append("committer T <t@e.com> ").Append(time).Append(" +0300\n");
        body.Append(extraHeaders).Append('\n').Append(message);
        var content = Encoding.UTF8.GetBytes(body.ToString());
        var obj = Encoding.ASCII.GetBytes($"commit {content.Length}\0").Concat(content).ToArray();
#pragma warning disable CA5350 // git's object id, not a security use
        var id = Convert.ToHexStringLower(SHA1.HashData(obj));
#pragma warning restore CA5350
        var dir = Path.Combine(gitDir, "objects", id[..2]);
        Directory.CreateDirectory(dir);
        using (var file = File.Create(Path.Combine(dir, id[2..])))
        using (var z = new ZLibStream(file, CompressionLevel.Fastest))
        {
            z.Write(obj);
        }

        return id;
    }

    private static string Chain(string gitDir, string from, int count, string prefix)
    {
        var id = from;
        for (var i = 0; i < count; i++)
        {
            id = Commit(gitDir, id, $"{prefix} {i}\n", 1_700_000_000 + i);
        }

        return id;
    }

    [Fact]
    public void Diverged_loose_history_is_read_without_a_large_buffer_per_commit()
    {
        var repo = NewRepo("diverged", out var gitDir);
        var root = Commit(gitDir, null, "root\n");
        SetRef(gitDir, "refs/heads/main", Chain(gitDir, root, GitMetadataReader.MaxAncestryWalk, "local"));
        SetRef(gitDir, "refs/remotes/origin/main", Chain(gitDir, root, GitMetadataReader.MaxAncestryWalk, "remote"));

        Assert.True(GitMetadataReader.Read(repo)!.AheadBehindUnknown); // warm-up (JIT, pools)

        var before = GC.GetAllocatedBytesForCurrentThread();
        var info = GitMetadataReader.Read(repo)!;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(info.AheadBehindUnknown); // neither side is an ancestor of the other: still unknown, never "in sync"
        Assert.Null(info.UnpushedCommits);
        Assert.Equal($"local {GitMetadataReader.MaxAncestryWalk - 1}", info.LastCommitSubject);

        // ~400 commit inflations: was ~94 MB (a fresh 256 KB array each, plus a copy); now a few KB of strings per commit.
        Assert.True(allocated < 12 * 1024 * 1024, $"Read() allocated {allocated / 1024} KB");
    }

    [Fact]
    public void Ahead_and_behind_by_loose_commits_are_proven()
    {
        var repo = NewRepo("aheadbehind", out var gitDir);
        var root = Commit(gitDir, null, "root\n");
        var upstream = Chain(gitDir, root, 5, "shared");
        SetRef(gitDir, "refs/remotes/origin/main", upstream);
        var local = Chain(gitDir, upstream, 3, "mine");
        SetRef(gitDir, "refs/heads/main", local);

        var ahead = GitMetadataReader.Read(repo)!;
        Assert.False(ahead.AheadBehindUnknown);
        Assert.Equal(3, ahead.Ahead);
        Assert.Equal(0, ahead.Behind);
        Assert.Equal(3, ahead.UnpushedCommits);
        Assert.Equal("mine 2", ahead.LastCommitSubject);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 22, 13, 20, TimeSpan.Zero).AddSeconds(2), ahead.LastCommitAt);
        Assert.Equal(TimeSpan.FromHours(3), ahead.LastCommitAt!.Value.Offset);

        SetRef(gitDir, "refs/remotes/origin/main", Chain(gitDir, local, 4, "theirs"));
        var behind = GitMetadataReader.Read(repo)!;
        Assert.False(behind.AheadBehindUnknown);
        Assert.Equal(0, behind.Ahead);
        Assert.Equal(4, behind.Behind);
    }

    [Fact]
    public void Commits_larger_than_the_first_buffer_are_still_parsed()
    {
        // A 40 KB signature header and a 100 KB message body: the buffer has to grow past its first 4 KB for the headers,
        // and the body after the subject line is never needed.
        var repo = NewRepo("big", out var gitDir);
        var root = Commit(gitDir, null, "root\n");
        SetRef(gitDir, "refs/remotes/origin/main", root);
        var sig = "gpgsig -----BEGIN PGP SIGNATURE-----\n" + string.Concat(Enumerable.Repeat(" " + new string('A', 63) + "\n", 640)) + " -----END PGP SIGNATURE-----\n";
        var big = Commit(gitDir, root, "Big signed commit\r\n\n" + new string('x', 100_000) + "\n", extraHeaders: sig);
        var head = Commit(gitDir, big, "on top\n");
        SetRef(gitDir, "refs/heads/main", head);

        var info = GitMetadataReader.Read(repo)!;
        Assert.False(info.AheadBehindUnknown); // the walk crossed the big commit's parent line
        Assert.Equal(2, info.Ahead);

        SetRef(gitDir, "refs/heads/main", big);
        var atBig = GitMetadataReader.Read(repo)!;
        Assert.Equal("Big signed commit", atBig.LastCommitSubject);
        Assert.Equal(1, atBig.Ahead);
    }

    [Theory]
    [InlineData("commit 9\0tree x\n\nsubj\nbody", 22)] // through the subject's newline
    [InlineData("commit 9\0tree x\n\nsubj", -1)] // subject line not finished yet
    [InlineData("commit 9\0tree x\nparent y\n", -1)] // headers not finished
    [InlineData("commit 9\0\nsubj\n", 15)] // no headers at all
    [InlineData("commit 9", -1)]
    public void Commit_prefix_stops_after_the_subject_line(string data, int expected) =>
        Assert.Equal(expected, GitMetadataReader.CommitPrefixLength(Encoding.ASCII.GetBytes(data)));

    [Fact]
    public void Local_state_reads_stashes_and_worktrees_like_the_full_reader()
    {
        var repo = NewRepo("local", out var gitDir);
        var root = Commit(gitDir, null, "root\n");
        SetRef(gitDir, "refs/heads/main", root);
        SetRef(gitDir, "refs/stash", root);
        Directory.CreateDirectory(Path.Combine(gitDir, "logs", "refs"));
        File.WriteAllText(Path.Combine(gitDir, "logs", "refs", "stash"), "a\nb\n");
        var wt = _dir.Sub("local", ".git", "worktrees", "feature");
        File.WriteAllText(Path.Combine(wt, "gitdir"), "C:/elsewhere/feature/.git\n");

        var full = GitMetadataReader.Read(repo)!;
        var local = GitMetadataReader.ReadLocalState(repo)!.Value;
        Assert.Equal(2, local.Stashes);
        Assert.Equal(full.Stashes, local.Stashes);
        Assert.Equal([@"C:\elsewhere\feature"], local.LinkedWorktrees);
        Assert.Equal(full.LinkedWorktrees, local.LinkedWorktrees);

        Assert.Null(GitMetadataReader.ReadLocalState(_dir.Sub("not-a-repo")));
    }
}
