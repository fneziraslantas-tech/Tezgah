using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Discovery;

namespace Tezgah.Core.Tests.Discovery;

/// <summary>Unsaved-work detection must look at every local branch, not only the checked-out one.</summary>
public sealed class UnpushedWorkTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static int Git(string dir, params string[] args) => TestTools.Run("git", dir, ["-c", "user.name=T", "-c", "user.email=t@e.com", "-c", "commit.gpgsign=false", .. args]);

    /// <summary>main pushed to a bare origin and in sync. Returns the clone path, or empty when git is missing.</summary>
    private string SyncedRepo()
    {
        var repo = _dir.Sub("repo");
        if (!TestTools.GitInitWithCommit(repo))
        {
            return string.Empty;
        }

        var bare = _dir.Sub("origin.git");
        Assert.Equal(0, Git(bare, "init", "-q", "--bare", "-b", "main"));
        Assert.Equal(0, Git(repo, "remote", "add", "origin", bare));
        Assert.Equal(0, Git(repo, "push", "-q", "-u", "origin", "main"));
        return repo;
    }

    [Fact]
    public async Task Local_only_branch_is_unsaved_work_even_when_head_is_clean()
    {
        var repo = SyncedRepo();
        if (repo.Length == 0)
        {
            return;
        }

        Assert.Equal(0, Git(repo, "checkout", "-q", "-b", "feature/x"));
        File.WriteAllText(Path.Combine(repo, "f.txt"), "work");
        Assert.Equal(0, Git(repo, "add", "-A"));
        Assert.Equal(0, Git(repo, "commit", "-q", "-m", "feature work"));
        Assert.Equal(0, Git(repo, "checkout", "-q", "main"));

        var info = await new GitClient(NullLogger<GitClient>.Instance).GetInfoAsync(repo, false, CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal(0, info!.Ahead);
        Assert.Equal(0, info.ModifiedFiles);
        Assert.Equal(1, info.UnpushedCommits);
        Assert.Contains("feature/x", info.UnpushedBranches);
        Assert.True(info.HasUnsavedWork);
    }

    [Fact]
    public async Task Synced_repository_has_no_unsaved_work()
    {
        var repo = SyncedRepo();
        if (repo.Length == 0)
        {
            return;
        }

        var info = await new GitClient(NullLogger<GitClient>.Instance).GetInfoAsync(repo, false, CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal(0, info!.UnpushedCommits);
        Assert.False(info.HasUnsavedWork);
    }

    [Fact]
    public async Task Linked_worktree_is_reported()
    {
        var repo = SyncedRepo();
        if (repo.Length == 0)
        {
            return;
        }

        Assert.Equal(0, Git(repo, "worktree", "add", "-q", Path.Combine(_dir.Path, "wt"), "-b", "wtb"));

        var info = await new GitClient(NullLogger<GitClient>.Instance).GetInfoAsync(repo, false, CancellationToken.None);

        Assert.Single(info!.LinkedWorktrees);
        Assert.True(info.HasUnsavedWork);
    }

    [Fact]
    public void Upstream_without_ahead_behind_line_is_gone()
    {
        var s = GitStatusParser.Parse("# branch.oid abc\n# branch.head feat\n# branch.upstream origin/feat\n");
        Assert.True(s.UpstreamGone);
        Assert.False(GitStatusParser.Parse("# branch.oid abc\n# branch.head feat\n# branch.upstream origin/feat\n# branch.ab +0 -0\n").UpstreamGone);
    }

    [Fact]
    public void Gone_upstream_counts_as_unsaved() =>
        Assert.True(new Model.GitInfo { RemoteUrl = "x", HasUpstream = true, UpstreamGone = true, LastCommitAt = DateTimeOffset.Now }.HasUnsavedWork);

    [Fact]
    public void For_each_ref_output_is_parsed() =>
        Assert.Equal(["local", "ahead", "gone"], GitClient.ParseUnpushedBranches(
            "main\0refs/remotes/origin/main\0\nlocal\0\0\nahead\0refs/remotes/origin/ahead\0[ahead 2]\ngone\0refs/remotes/origin/gone\0[gone]\n"));
}
