using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Discovery;

namespace Tezgah.Core.Tests.Discovery;

/// <summary>Repository-controlled git config must never run code during Tezgah's own git calls.</summary>
public sealed class GitHardeningTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static string ShPath(string p) => p.Replace('\\', '/');

    private string NewRepo(string name = "repo")
    {
        var repo = _dir.Sub(name);
        return TestTools.GitInitWithCommit(repo) ? repo : string.Empty;
    }

    [Fact]
    public async Task RepoLocalFsmonitorIsNotExecutedByGetInfo()
    {
        var repo = NewRepo();
        if (repo.Length == 0)
        {
            return; // git not installed
        }

        var marker = Path.Combine(_dir.Path, "fsmonitor-ran.txt");
        Assert.Equal(0, TestTools.Run("git", repo, "config", "core.fsmonitor", $"echo PWNED> '{ShPath(marker)}'; false"));
        File.WriteAllText(Path.Combine(repo, "README.md"), "changed");

        var info = await new GitClient(NullLogger<GitClient>.Instance).GetInfoAsync(repo, fetch: false, CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal(1, info!.ModifiedFiles);
        Assert.False(File.Exists(marker), "core.fsmonitor from the repository config was executed");
    }

    [Fact]
    public async Task FsmonitorFromIncludedConfigIsNotExecuted()
    {
        var repo = NewRepo();
        if (repo.Length == 0)
        {
            return;
        }

        var marker = Path.Combine(_dir.Path, "include-ran.txt");
        File.WriteAllText(Path.Combine(repo, ".git", "evil.cfg"), $"[core]\n\tfsmonitor = \"echo PWNED> '{ShPath(marker)}'; false\"\n");
        File.AppendAllText(Path.Combine(repo, ".git", "config"), "[include]\n\tpath = evil.cfg\n");

        var info = await new GitClient(NullLogger<GitClient>.Instance).GetInfoAsync(repo, fetch: false, CancellationToken.None);

        Assert.NotNull(info);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task PullDoesNotRunRepositoryHooks()
    {
        if (!TestTools.HasGit)
        {
            return;
        }

        var bare = _dir.Sub("origin.git");
        Assert.Equal(0, TestTools.Run("git", bare, "init", "-q", "--bare", "-b", "main"));
        var a = NewRepo("a");
        Assert.Equal(0, TestTools.Run("git", a, "remote", "add", "origin", bare));
        Assert.Equal(0, TestTools.Run("git", a, "push", "-q", "-u", "origin", "main"));
        var b = Path.Combine(_dir.Path, "b");
        Assert.Equal(0, TestTools.Run("git", _dir.Path, "clone", "-q", bare, b));

        File.WriteAllText(Path.Combine(a, "new.txt"), "x");
        Assert.Equal(0, TestTools.Run("git", a, "add", "-A"));
        Assert.Equal(0, TestTools.Run("git", a, "-c", "user.name=T", "-c", "user.email=t@e.com", "commit", "-q", "-m", "second"));
        Assert.Equal(0, TestTools.Run("git", a, "push", "-q"));

        var marker = Path.Combine(_dir.Path, "hook-ran.txt");
        File.WriteAllText(Path.Combine(b, ".git", "hooks", "post-merge"), $"#!/bin/sh\necho ran > '{ShPath(marker)}'\n");

        var result = await new GitClient(NullLogger<GitClient>.Instance).PullAsync(b, CancellationToken.None);

        Assert.True(result.Success, result.StdErr);
        Assert.True(File.Exists(Path.Combine(b, "new.txt")));
        Assert.False(File.Exists(marker), "post-merge hook from the repository ran");
    }

    [Fact]
    public async Task GitFilePointingToUncReturnsNullWithoutContactingShare()
    {
        var git = new GitClient(NullLogger<GitClient>.Instance);
        if (!await git.IsAvailableAsync(CancellationToken.None))
        {
            return;
        }

        var folder = _dir.Sub("unc");
        File.WriteAllText(Path.Combine(folder, ".git"), "gitdir: //203.0.113.5/x/repo\n");

        var sw = Stopwatch.StartNew();
        Assert.Null(await git.GetInfoAsync(folder, fetch: false, CancellationToken.None));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "git appears to have tried the network path");
    }

    [Theory]
    [InlineData("gitdir: //203.0.113.5/x/repo")]
    [InlineData(@"gitdir: \\203.0.113.5\x\repo")]
    [InlineData(@"gitdir: \\?\UNC\host\share\repo")]
    [InlineData(@"gitdir: \\.\pipe\x")]
    public void GuardRejectsNonLocalGitFilePointers(string content)
    {
        var folder = _dir.Sub("p");
        File.WriteAllText(Path.Combine(folder, ".git"), content + "\n");
        Assert.False(GitRepoGuard.TryResolveLocalGitDir(folder, out _, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void GuardAcceptsLocalRelativeWorktreePointer()
    {
        var real = _dir.Sub("main", ".git", "worktrees", "wt");
        var wt = _dir.Sub("wt");
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: ../main/.git/worktrees/wt\n");
        File.WriteAllText(Path.Combine(real, "commondir"), "../..\n");
        File.WriteAllText(Path.Combine(real, "gitdir"), Path.Combine(wt, ".git").Replace('\\', '/') + "\n");
        File.WriteAllText(Path.Combine(real, "HEAD"), "ref: refs/heads/wt\n");
        File.WriteAllText(Path.Combine(_dir.Path, "main", ".git", "HEAD"), "ref: refs/heads/main\n");
        Assert.True(GitRepoGuard.TryResolveLocalGitDir(wt, out var gitDir, out _));
        Assert.Equal(Path.GetFullPath(real), gitDir);
    }

    [Fact]
    public void GuardRejectsUncAlternatesAndIncludes()
    {
        var a = _dir.Sub("alt", ".git", "objects", "info");
        File.WriteAllText(Path.Combine(a, "alternates"), @"\\host\share\objects" + "\n");
        Assert.False(GitRepoGuard.TryResolveLocalGitDir(_dir.Sub("alt"), out _, out _));

        var i = _dir.Sub("inc", ".git");
        File.WriteAllText(Path.Combine(i, "config"), "[core]\n\tbare = false\n[includeIf \"gitdir:~/\"]\n\tpath = //host/share/x.cfg\n");
        Assert.False(GitRepoGuard.TryResolveLocalGitDir(_dir.Sub("inc"), out _, out _));

        // Strict: even a local include is refused (git would read config the guard never parsed).
        var local = _dir.Sub("localinc", ".git");
        File.WriteAllText(Path.Combine(local, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(local, "config"), "[include]\n\tpath = local.cfg\n");
        Assert.False(GitRepoGuard.TryResolveLocalGitDir(_dir.Sub("localinc"), out _, out _));

        var ok = _dir.Sub("ok", ".git");
        File.WriteAllText(Path.Combine(ok, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(ok, "config"), "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = https://example.com/x.git\n");
        Assert.True(GitRepoGuard.TryResolveLocalGitDir(_dir.Sub("ok"), out _, out _));
    }

    [Fact]
    public void EveryCallCarriesHardeningConfigAndNeutralArguments()
    {
        var env = GitClient.BuildEnvironment(@"C:\src\repo", ["x"]);
        var count = int.Parse(env["GIT_CONFIG_COUNT"], System.Globalization.CultureInfo.InvariantCulture);
        var pairs = Enumerable.Range(0, count).Select(i => (env[$"GIT_CONFIG_KEY_{i}"], env[$"GIT_CONFIG_VALUE_{i}"])).ToList();
        Assert.Contains(("core.fsmonitor", "false"), pairs);
        Assert.Contains(("core.hooksPath", "NUL"), pairs);
        Assert.Contains(("protocol.ext.allow", "never"), pairs);
        Assert.Contains(("filter.x.clean", string.Empty), pairs);
        Assert.Contains(("filter.x.process", string.Empty), pairs);
        Assert.Contains(("log.showSignature", "false"), pairs);
        Assert.Contains(("gpg.program", GitClient.DisabledProgram), pairs);
        Assert.Contains(("gpg.ssh.program", GitClient.DisabledProgram), pairs);
        Assert.Contains(("gpg.x509.program", GitClient.DisabledProgram), pairs);
        Assert.Contains(("diff.ignoreSubmodules", "all"), pairs);
        Assert.Contains(("credential.helper", string.Empty), pairs);
        Assert.Contains(("core.sshCommand", string.Empty), pairs);
        Assert.False(File.Exists(GitClient.DisabledProgram));

        // Network calls (trusted fetch/pull/clone) keep the user's credential setup.
        var net = GitClient.BuildEnvironment(@"C:\src\repo", [], network: true);
        var netPairs = Enumerable.Range(0, int.Parse(net["GIT_CONFIG_COUNT"], System.Globalization.CultureInfo.InvariantCulture)).Select(i => net[$"GIT_CONFIG_KEY_{i}"]).ToList();
        Assert.DoesNotContain("credential.helper", netPairs);
        Assert.DoesNotContain("core.excludesFile", netPairs);
        Assert.Contains(("core.attributesFile", string.Empty), pairs);
        Assert.Contains(pairs, p => p.Item1 == "core.excludesFile" && !GitRepoGuard.LooksRemote(p.Item2));
        Assert.Contains("log.showSignature", netPairs);
        Assert.Equal(@"C:\src", env["GIT_CEILING_DIRECTORIES"]);

        var args = GitClient.BuildArguments(@"C:\src\repo", ["status"]);
        Assert.Equal(["--no-pager", "--no-optional-locks", "-C", @"C:\src\repo", "status"], args);
    }

    [Fact]
    public void PinnedExcludesFileIsTheUsersOwnAndLocal()
    {
        // Only the user's config decides the pinned excludes file; a share or a runtime-prefix value pins nothing.
        var home = _dir.Sub("home");
        var xdg = Path.Combine(home, ".config");
        Assert.Equal(Path.Combine(xdg, "git", "ignore"), GitClient.ResolveUserExcludesFile(home, xdg));

        File.WriteAllText(Path.Combine(home, ".gitconfig"), "[core]\n\texcludesFile = ~/.gitignore_global\n");
        Assert.Equal(Path.Combine(home, ".gitignore_global"), GitClient.ResolveUserExcludesFile(home, xdg));

        File.WriteAllText(Path.Combine(home, ".gitconfig"), "[core]\n\texcludesFile = :(optional)//192.0.2.77/share/x\n");
        Assert.Equal(string.Empty, GitClient.ResolveUserExcludesFile(home, xdg));

        File.WriteAllText(Path.Combine(home, ".gitconfig"), "[core]\n\texcludesFile = %(prefix)/etc/ignore\n");
        Assert.Equal(string.Empty, GitClient.ResolveUserExcludesFile(home, xdg));
    }

    [Fact]
    public void FilterDriverNamesAreParsed() =>
        Assert.Equal(["lfs", "my.odd=name"], GitClient.FilterDriverNames("filter.lfs.clean\0filter.lfs.process\0filter.my.odd=name.smudge\0other.x\0"));

    [Fact]
    public void CloudPlaceholderGitDataIsDetected()
    {
        var repo = _dir.Sub("cloud");
        _dir.Sub("cloud", ".git", "objects", "pack");
        File.WriteAllText(Path.Combine(repo, ".git", "index"), "x");
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        Assert.False(ProjectInspector.IsCloudBackedRepo(repo));

        FileAttributes Fake(string p) => p.EndsWith("index", StringComparison.Ordinal) ? (FileAttributes)0x00400000 : FileAttributes.Normal;
        Assert.True(ProjectInspector.IsCloudBackedRepo(repo, Fake));
    }
}
