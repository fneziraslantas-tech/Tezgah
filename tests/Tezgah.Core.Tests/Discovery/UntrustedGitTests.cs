using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Discovery;

namespace Tezgah.Core.Tests.Discovery;

/// <summary>
/// Crafted repositories whose own config makes git run a program during read-only commands (reviewer PoCs). Each test
/// first proves the PoC works against plain git, then that Tezgah's untrusted scan starts no git process at all and that
/// the git CLI path (trusted items / uninstall planning) never runs the marker program either.
/// </summary>
public sealed class UntrustedGitTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static string Sh(string p) => p.Replace('\\', '/');

    private string Marker(string name) => Path.Combine(_dir.Path, name + "-ran.txt");

    /// <summary>A shell script git for Windows can start as a program; it records that it ran and passes stdin through.</summary>
    private string MarkerProgram(string name)
    {
        var script = Path.Combine(_dir.Path, name + "-prog");
        File.WriteAllText(script, $"#!/bin/sh\necho ran > '{Sh(Marker(name))}'\ncat\n");
        return script;
    }

    private static string GitOut(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return output.Trim();
    }

    private static ProjectInspector Inspector(GitClient git) => DetectorTests.NewInspector(git);

    /// <summary>Background scan of an untrusted repository: metadata only, and not a single git process.</summary>
    private static async Task AssertUntrustedScanStartsNoGitAsync(string repo)
    {
        var git = new GitClient(NullLogger<GitClient>.Instance);
        var item = await Inspector(git).InspectAsync(repo, fetchGit: false, force: true, computeSize: false, CancellationToken.None, skipCloudGit: true);
        Assert.NotNull(item?.Git);
        Assert.True(item!.Git!.StatusUnknown);
        Assert.True(item.Git.HasUnsavedWork); // unknown counts as unsaved for deletion safety
        Assert.Equal(0, git.ProcessesStarted);
    }

    [Fact]
    public async Task Signature_verification_program_never_runs()
    {
        var repo = _dir.Sub("signed");
        if (!TestTools.GitInitWithCommit(repo))
        {
            return; // git not installed
        }

        // A commit with a gpgsig header, written directly into the object store.
        var tree = GitOut(repo, "rev-parse", "HEAD^{tree}");
        var parent = GitOut(repo, "rev-parse", "HEAD");
        var raw = Path.Combine(_dir.Path, "commit.txt");
        File.WriteAllText(raw,
            $"tree {tree}\nparent {parent}\nauthor T <t@e.com> 1700000000 +0300\ncommitter T <t@e.com> 1700000000 +0300\n" +
            "gpgsig -----BEGIN PGP SIGNATURE-----\n \n iQEzBAABCAAdFiEEAAAAAAAAAAAAAAAAAAAAAAAAAAAFAmVVVVUACgkQAAAAAAAAAAA=\n =AAAA\n -----END PGP SIGNATURE-----\n" +
            "\nsigned subject\n");
        var signed = GitOut(repo, "hash-object", "-t", "commit", "-w", raw);
        Assert.Equal(0, TestTools.Run("git", repo, "update-ref", "refs/heads/main", signed));
        Assert.Equal(0, TestTools.Run("git", repo, "config", "log.showSignature", "true"));
        Assert.Equal(0, TestTools.Run("git", repo, "config", "gpg.program", Sh(MarkerProgram("gpg"))));

        // PoC: plain "git log -1" runs gpg.program.
        TestTools.Run("git", repo, "log", "-1", "--format=%s");
        Assert.True(File.Exists(Marker("gpg")), "precondition: the crafted repository should make plain git run gpg.program");
        File.Delete(Marker("gpg"));

        await AssertUntrustedScanStartsNoGitAsync(repo);
        Assert.Equal("signed subject", GitMetadataReader.Read(repo)!.LastCommitSubject); // read from the loose object

        var info = await new GitClient(NullLogger<GitClient>.Instance).GetInfoAsync(repo, fetch: false, CancellationToken.None);
        Assert.NotNull(info);
        Assert.False(info!.StatusUnknown); // validated repository: the hardened CLI ran
        Assert.Equal("signed subject", info.LastCommitSubject);
        Assert.False(File.Exists(Marker("gpg")), "gpg.program from the repository config was executed");
    }

    [Fact]
    public async Task Submodule_clean_filter_never_runs()
    {
        var source = _dir.Sub("subsrc");
        var parent = _dir.Sub("parent");
        if (!TestTools.GitInitWithCommit(source) || !TestTools.GitInitWithCommit(parent))
        {
            return;
        }

        Assert.Equal(0, TestTools.Run("git", parent, "-c", "protocol.file.allow=always", "submodule", "add", "-q", source, "sub"));
        Assert.Equal(0, TestTools.Run("git", parent, "-c", "user.name=T", "-c", "user.email=t@e.com", "commit", "-q", "-m", "add sub"));

        // The submodule's own config (inside the parent's .git/modules) defines a clean filter applied to every file.
        File.AppendAllText(Path.Combine(parent, ".git", "modules", "sub", "config"), $"[filter \"evil\"]\n\tclean = {Sh(MarkerProgram("clean"))}\n");
        var sub = Path.Combine(parent, "sub");
        File.WriteAllText(Path.Combine(sub, ".gitattributes"), "* filter=evil\n");
        var readme = Path.Combine(sub, "README.md");
        File.SetLastWriteTimeUtc(readme, DateTime.UtcNow.AddMinutes(5)); // stat-dirty, same size: git must run the filter to compare

        TestTools.Run("git", parent, "status");
        Assert.True(File.Exists(Marker("clean")), "precondition: plain git status of the parent should run the submodule's clean filter");
        File.Delete(Marker("clean"));
        File.SetLastWriteTimeUtc(readme, DateTime.UtcNow.AddMinutes(10));

        await AssertUntrustedScanStartsNoGitAsync(parent);

        var info = await new GitClient(NullLogger<GitClient>.Instance).GetInfoAsync(parent, fetch: false, CancellationToken.None);
        Assert.NotNull(info);
        Assert.False(File.Exists(Marker("clean")), "a submodule filter driver ran during git status of the parent");
    }

    [Fact]
    public async Task Include_hidden_behind_a_continuation_line_is_refused()
    {
        var repo = _dir.Sub("cont");
        if (!TestTools.GitInitWithCommit(repo))
        {
            return;
        }

        var evil = Path.Combine(_dir.Path, "evil.cfg");
        File.WriteAllText(evil, $"[core]\n\tfsmonitor = \"echo PWNED> '{Sh(Marker("cont"))}'; false\"\n");
        File.AppendAllText(Path.Combine(repo, ".git", "config"), $"[include]\n\tpath = \\\n{Sh(evil)}\n");
        File.WriteAllText(Path.Combine(repo, "README.md"), "changed");

        TestTools.Run("git", repo, "status");
        Assert.True(File.Exists(Marker("cont")), "precondition: git should follow the include hidden behind the continuation line");
        File.Delete(Marker("cont"));

        Assert.False(GitRepoGuard.TryResolveLocalGitDir(repo, out _, out var reason));
        Assert.Contains("continues", reason, StringComparison.Ordinal);
        await AssertRefusedWithoutGitAsync(repo);
        await AssertUntrustedScanStartsNoGitAsync(repo);
        Assert.False(File.Exists(Marker("cont")));
    }

    [Fact]
    public async Task Nested_include_is_refused()
    {
        var repo = _dir.Sub("nested");
        if (!TestTools.GitInitWithCommit(repo))
        {
            return;
        }

        var gitDir = Path.Combine(repo, ".git");
        File.WriteAllText(Path.Combine(gitDir, "b.cfg"), $"[core]\n\tfsmonitor = \"echo PWNED> '{Sh(Marker("nested"))}'; false\"\n");
        File.WriteAllText(Path.Combine(gitDir, "a.cfg"), "[include]\n\tpath = b.cfg\n");
        File.AppendAllText(Path.Combine(gitDir, "config"), "[include]\n\tpath = a.cfg\n");
        File.WriteAllText(Path.Combine(repo, "README.md"), "changed");

        TestTools.Run("git", repo, "status");
        Assert.True(File.Exists(Marker("nested")), "precondition: git should follow the nested include");
        File.Delete(Marker("nested"));

        Assert.False(GitRepoGuard.TryResolveLocalGitDir(repo, out _, out _));
        await AssertRefusedWithoutGitAsync(repo);
        await AssertUntrustedScanStartsNoGitAsync(repo);
        Assert.False(File.Exists(Marker("nested")));
    }

    [Fact]
    public async Task Core_worktree_and_per_worktree_config_are_refused()
    {
        var repo = _dir.Sub("wtcfg");
        if (!TestTools.GitInitWithCommit(repo))
        {
            return;
        }

        var elsewhere = _dir.Sub("elsewhere");
        Assert.Equal(0, TestTools.Run("git", repo, "config", "core.worktree", Sh(elsewhere)));
        Assert.False(GitRepoGuard.TryResolveLocalGitDir(repo, out _, out var reason));
        Assert.Contains("core.worktree", reason, StringComparison.Ordinal);
        await AssertRefusedWithoutGitAsync(repo);
        await AssertUntrustedScanStartsNoGitAsync(repo);

        Assert.Equal(0, TestTools.Run("git", repo, "config", "--unset", "core.worktree"));
        Assert.True(GitRepoGuard.TryResolveLocalGitDir(repo, out _, out _));
        File.WriteAllText(Path.Combine(repo, ".git", "config.worktree"), "[core]\n\tfsmonitor = x\n");
        Assert.False(GitRepoGuard.TryResolveLocalGitDir(repo, out _, out _));
    }

    [Fact]
    public void Chained_and_quoted_alternates_are_refused()
    {
        var a = _dir.Sub("a", ".git", "objects", "info");
        File.WriteAllText(Path.Combine(_dir.Path, "a", ".git", "HEAD"), "ref: refs/heads/main\n");
        var b = _dir.Sub("b-objects", "info");
        var c = _dir.Sub("c-objects", "info");
        File.WriteAllText(Path.Combine(a, "alternates"), Sh(Path.Combine(_dir.Path, "b-objects")) + "\n");
        Assert.True(GitRepoGuard.TryResolveLocalGitDir(_dir.Sub("a"), out _, out _));

        File.WriteAllText(Path.Combine(b, "alternates"), Sh(Path.Combine(_dir.Path, "c-objects")) + "\n");
        Assert.False(GitRepoGuard.TryResolveLocalGitDir(_dir.Sub("a"), out _, out var reason));
        Assert.Contains("chained", reason, StringComparison.Ordinal);
        Assert.True(Directory.Exists(c));

        File.WriteAllText(Path.Combine(a, "alternates"), "\"\\\\host\\share\\objects\"\n");
        Assert.False(GitRepoGuard.TryResolveLocalGitDir(_dir.Sub("a"), out _, out _));
    }

    [Theory]
    [InlineData("[core]\n\tbare = false\n", true)]
    [InlineData("[include]\n\tpath = \\\n//host/share/x\n", false)]
    [InlineData("[core]\n\tbare = \"unterminated\n", false)]
    [InlineData("[core\n", false)]
    [InlineData("[core]\n\texcludesFile = //host/share/ignore\n", false)]
    [InlineData("[core]\n\texcludesFile = \\\\\\\\host\\\\share\\\\ignore\n", false)]
    [InlineData("[remote \"origin\"]\n\turl = https://example.com/x.git ; comment\n", true)]
    [InlineData("\uFEFF[core]\n\tbare\n", true)]
    public void Strict_guard_fails_closed_on_config_it_cannot_parse_or_that_points_away(string config, bool accepted)
    {
        var repo = _dir.Sub("cfg" + Guid.NewGuid().ToString("N")[..6]);
        var gitDir = _dir.Sub(Path.GetFileName(repo), ".git");
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(gitDir, "config"), config);
        Assert.Equal(accepted, GitRepoGuard.TryResolveLocalGitDir(repo, out _, out _));
    }

    [Fact]
    public void Config_parser_matches_git_for_common_syntax()
    {
        const string text = "[core]\n\tbare = false\n[branch \"feat/x\"]\n\tremote = origin\n\tmerge = refs/heads/feat/x\n" +
            "[remote \"origin\"]\n\turl = \"https://h/x.git\" # c\n[Section.Sub]\n\tKey = a\\tb  \n";
        Assert.True(GitConfigParser.TryParse(text, strict: true, out var entries, out _));
        Assert.Contains(entries, e => e.Key == "branch.feat/x.merge" && e.Value == "refs/heads/feat/x");
        Assert.Contains(entries, e => e.Key == "remote.origin.url" && e.Value == "https://h/x.git");
        Assert.Contains(entries, e => e.Key == "section.sub.key" && e.Value == "a\tb");

        Assert.False(GitConfigParser.TryParse("[a]\n\tk = x\\\ny\n", strict: true, out _, out _));
        Assert.True(GitConfigParser.TryParse("[a]\n\tk = x\\\ny\n", strict: false, out var tolerant, out _));
        Assert.Equal("xy", Assert.Single(tolerant).Value);
    }

    [Fact]
    public async Task Metadata_reader_reports_branch_upstream_ahead_and_stash_without_git()
    {
        if (!TestTools.HasGit)
        {
            return;
        }

        var bare = _dir.Sub("origin.git");
        Assert.Equal(0, TestTools.Run("git", bare, "init", "-q", "--bare", "-b", "main"));
        var repo = _dir.Sub("work");
        Assert.True(TestTools.GitInitWithCommit(repo));
        Assert.Equal(0, TestTools.Run("git", repo, "remote", "add", "origin", bare));
        Assert.Equal(0, TestTools.Run("git", repo, "push", "-q", "-u", "origin", "main"));

        var clean = GitMetadataReader.Read(repo)!;
        Assert.Equal("main", clean.Branch);
        Assert.True(clean.HasUpstream);
        Assert.False(clean.UpstreamGone);
        Assert.Equal(0, clean.Ahead);
        Assert.Equal(0, clean.UnpushedCommits);
        Assert.Equal(0, clean.Stashes);
        Assert.Equal("initial commit", clean.LastCommitSubject);
        Assert.Equal(Sh(bare), Sh(clean.RemoteUrl!));
        Assert.True(clean.StatusUnknown);
        Assert.False(clean.HasKnownUnsavedWork);
        Assert.True(clean.HasUnsavedWork);

        File.WriteAllText(Path.Combine(repo, "b.txt"), "b");
        Assert.Equal(0, TestTools.Run("git", repo, "add", "-A"));
        Assert.Equal(0, TestTools.Run("git", repo, "-c", "user.name=T", "-c", "user.email=t@e.com", "commit", "-q", "-m", "second"));
        File.WriteAllText(Path.Combine(repo, "README.md"), "wip");
        Assert.Equal(0, TestTools.Run("git", repo, "-c", "user.name=T", "-c", "user.email=t@e.com", "stash", "-q"));

        var ahead = GitMetadataReader.Read(repo)!;
        Assert.Equal(1, ahead.Ahead);
        Assert.Equal(1, ahead.UnpushedCommits);
        Assert.Equal(1, ahead.Stashes);
        Assert.Equal("second", ahead.LastCommitSubject);
        Assert.True(ahead.HasKnownUnsavedWork);

        // The CLI agrees on what both can see.
        var cli = await new GitClient(NullLogger<GitClient>.Instance).GetInfoAsync(repo, fetch: false, CancellationToken.None);
        Assert.Equal(cli!.Ahead, ahead.Ahead);
        Assert.Equal(cli.Stashes, ahead.Stashes);
        Assert.Equal(cli.Branch, ahead.Branch);
    }

    [Theory]
    [InlineData("gitdir: //203.0.113.5/x/repo")]
    [InlineData(@"gitdir: \\?\UNC\host\share\repo")]
    [InlineData("gitdir: D:")]
    [InlineData(@"gitdir: C:\")]
    [InlineData(@"gitdir: \Windows")]
    public void Metadata_reader_never_follows_non_local_or_drive_tricks(string gitFile)
    {
        var folder = _dir.Sub("ptr" + Math.Abs(gitFile.GetHashCode(StringComparison.Ordinal)));
        File.WriteAllText(Path.Combine(folder, ".git"), gitFile + "\n");
        var sw = Stopwatch.StartNew();
        Assert.Null(GitMetadataReader.Read(folder));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "the reader appears to have touched a network path");
    }

    private static async Task AssertRefusedWithoutGitAsync(string repo)
    {
        // Uninstall planning path: the guard refuses, so no git process runs and the state is unknown (fail closed).
        var git = new GitClient(NullLogger<GitClient>.Instance);
        var info = await git.GetInfoAsync(repo, fetch: false, CancellationToken.None);
        Assert.Equal(0, git.ProcessesStarted);
        Assert.NotNull(info);
        Assert.True(info!.StatusUnknown);
        Assert.True(info.HasUnsavedWork);
    }
}
