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

    [Theory]
    [InlineData("gitdir", "//10.255.255.7/share/wt/.git")]
    [InlineData("gitdir", @"\\10.255.255.7\share\wt\.git")]
    [InlineData("gitdir", @"\\?\UNC\10.255.255.7\share\wt\.git")]
    [InlineData("gitdir", "D:wt/.git")]
    [InlineData("commondir", "//10.255.255.7/share/x")]
    [InlineData("gitdir", "")]
    public async Task Crafted_linked_worktree_back_pointer_is_refused_without_starting_git(string file, string value)
    {
        // Reviewer PoC: "git worktree list" (run during uninstall planning) stats every worktrees/<id>/gitdir path, so a
        // //host/share value makes git open an SMB connection without the repository ever being trusted. Plain git is
        // deliberately not run against the address here.
        var repo = _dir.Sub("wtptr" + Math.Abs((file + value).GetHashCode(StringComparison.Ordinal)));
        if (!TestTools.GitInitWithCommit(repo))
        {
            return;
        }

        var entry = _dir.Sub(Path.GetFileName(repo), ".git", "worktrees", "x");
        File.WriteAllText(Path.Combine(entry, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(entry, "commondir"), "../..\n");
        File.WriteAllText(Path.Combine(entry, "gitdir"), Sh(Path.Combine(_dir.Path, "wt", ".git")) + "\n");
        Assert.True(GitRepoGuard.TryResolveLocalGitDir(repo, out _, out _)); // a local back-pointer is fine

        File.WriteAllText(Path.Combine(entry, file), value + "\n");
        Assert.False(GitRepoGuard.TryResolveLocalGitDir(repo, out _, out var reason));
        Assert.Contains("worktree", reason, StringComparison.Ordinal);

        var sw = Stopwatch.StartNew();
        await AssertRefusedWithoutGitAsync(repo);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "the uninstall-planning git path appears to have touched the network path");
    }

    [Fact]
    public async Task Linked_worktrees_come_from_the_managed_reader_on_the_cli_path()
    {
        var repo = _dir.Sub("wtcli");
        if (!TestTools.GitInitWithCommit(repo))
        {
            return;
        }

        var wt = Path.Combine(_dir.Path, "wtcli-linked");
        Assert.Equal(0, TestTools.Run("git", repo, "worktree", "add", "-q", wt, "-b", "side"));
        var git = new GitClient(NullLogger<GitClient>.Instance);
        var info = await git.GetInfoAsync(repo, fetch: false, CancellationToken.None);
        Assert.False(info!.StatusUnknown);
        Assert.Equal(Path.GetFullPath(wt), Path.GetFullPath(Assert.Single(info.LinkedWorktrees)), ignoreCase: true);
        Assert.True(info.HasUnsavedWork);
    }

    [Fact]
    public async Task Packed_head_commit_never_reports_the_reflog_time_as_commit_time()
    {
        // Reviewer PoC: with HEAD's commit packed (clone, gc), the newest HEAD reflog entry is clone/checkout/pull time.
        var source = _dir.Sub("packsrc");
        if (!TestTools.GitInitWithCommit(source))
        {
            return;
        }

        // An old commit: committer date years ago.
        File.WriteAllText(Path.Combine(source, "old.txt"), "old");
        Assert.Equal(0, TestTools.Run("git", source, "add", "-A"));
        var psi = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = source };
        foreach (var a in new[] { "-c", "user.name=T", "-c", "user.email=t@e.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "old work" })
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["GIT_COMMITTER_DATE"] = "2019-03-04T05:06:07+0300";
        psi.Environment["GIT_AUTHOR_DATE"] = "2019-03-04T05:06:07+0300";
        using (var p = Process.Start(psi)!)
        {
            p.WaitForExit(60_000);
            Assert.Equal(0, p.ExitCode);
        }

        var clone = Path.Combine(_dir.Path, "packclone");
        Assert.Equal(0, TestTools.Run("git", _dir.Path, "clone", "-q", "--no-local", source, clone));
        Assert.False(File.Exists(Path.Combine(clone, ".git", "objects", GitOut(clone, "rev-parse", "HEAD")[..2], GitOut(clone, "rev-parse", "HEAD")[2..])),
            "precondition: a clone keeps HEAD's commit packed");

        var cloned = GitMetadataReader.Read(clone)!;
        Assert.Null(cloned.LastCommitAt); // the reflog says "clone: ...", which is not the commit time
        Assert.Null(cloned.LastCommitSubject);
        Assert.True(cloned.HasCommits);

        var item = await Inspector(new GitClient(NullLogger<GitClient>.Instance)).InspectAsync(clone, fetchGit: false, force: true, computeSize: false, CancellationToken.None);
        Assert.NotNull(item);
        Assert.Null(item!.Git!.LastCommitAt);

        // Commit made here, then packed by gc: the matching "commit:" reflog entry is the commit time.
        File.WriteAllText(Path.Combine(source, "new.txt"), "new");
        Assert.Equal(0, TestTools.Run("git", source, "add", "-A"));
        Assert.Equal(0, TestTools.Run("git", source, "-c", "user.name=T", "-c", "user.email=t@e.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "fresh work"));
        Assert.Equal(0, TestTools.Run("git", source, "gc", "-q"));
        var head = GitOut(source, "rev-parse", "HEAD");
        Assert.False(File.Exists(Path.Combine(source, ".git", "objects", head[..2], head[2..])), "precondition: gc packs HEAD's commit");
        var packed = GitMetadataReader.Read(source)!;
        Assert.Equal(DateTimeOffset.Parse(GitOut(source, "log", "-1", "--format=%cI"), System.Globalization.CultureInfo.InvariantCulture), packed.LastCommitAt);
        Assert.Equal("fresh work", packed.LastCommitSubject);

        // HEAD moved by a checkout afterwards: the newest entry is no longer a commit record.
        Assert.Equal(0, TestTools.Run("git", source, "checkout", "-q", "HEAD~1"));
        var moved = GitMetadataReader.Read(source)!;
        Assert.Null(moved.LastCommitAt);
        Assert.True(moved.HasCommits);
    }

    [Fact]
    public void Unprovable_ahead_behind_is_reported_as_unknown_not_in_sync()
    {
        // Reviewer PoC: a clone that is behind its upstream with packed commits read Ahead=0/Behind=0 ("in sync").
        var source = _dir.Sub("absrc");
        if (!TestTools.GitInitWithCommit(source))
        {
            return;
        }

        File.WriteAllText(Path.Combine(source, "two.txt"), "2");
        Assert.Equal(0, TestTools.Run("git", source, "add", "-A"));
        Assert.Equal(0, TestTools.Run("git", source, "-c", "user.name=T", "-c", "user.email=t@e.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "two"));
        var clone = Path.Combine(_dir.Path, "abclone");
        Assert.Equal(0, TestTools.Run("git", _dir.Path, "clone", "-q", "--no-local", source, clone));

        var synced = GitMetadataReader.Read(clone)!;
        Assert.False(synced.AheadBehindUnknown); // HEAD == upstream: proven

        Assert.Equal(0, TestTools.Run("git", clone, "reset", "-q", "--hard", "HEAD~1")); // behind 1, commits packed
        var behind = GitMetadataReader.Read(clone)!;
        Assert.Equal(0, behind.Ahead);
        Assert.Equal(0, behind.Behind);
        Assert.True(behind.AheadBehindUnknown);

        var noUpstream = _dir.Sub("abnone");
        Assert.True(TestTools.GitInitWithCommit(noUpstream));
        Assert.False(GitMetadataReader.Read(noUpstream)!.AheadBehindUnknown);
    }

    [Theory]
    [InlineData(@"excludesFile = \\??\\UNC\\192.0.2.77\\share\\x")] // as `git config core.excludesFile '\??\UNC\...'` writes it
    [InlineData(@"excludesFile = \\??\\GLOBALROOT\\Device\\Mup\\192.0.2.77\\share\\x")]
    [InlineData(@"attributesFile = /??/UNC/192.0.2.77/share/x")]
    [InlineData(@"excludesFile = ""\\??\\unc\\192.0.2.77\\share\\x""")]
    [InlineData(@"excludesFile = \\\\?\\UNC\\192.0.2.77\\share\\x")]
    [InlineData(@"excludesFile = \\\\.\\UNC\\192.0.2.77\\share\\x")]
    [InlineData(@"excludesFile = %(prefix)//192.0.2.77/share/x")]
    [InlineData(@"excludesFile = C:\\x\\GLOBALROOT\\y")]
    [InlineData("excludesFile = :(optional)//192.0.2.77/share/x")] // git 2.52+ strips ":(optional)" from pathname values
    [InlineData("attributesFile = :(optional)//192.0.2.77/share/x")]
    [InlineData("excludesFile = :(optional)%(prefix)//192.0.2.77/share/x")]
    [InlineData("attributesFile = %(prefix):(optional)//192.0.2.77/share/x")]
    [InlineData(@"excludesFile = :(optional):(optional)\\\\192.0.2.77\\share\\x")]
    public async Task Nt_namespace_config_value_is_refused_before_git_runs(string line)
    {
        // Reviewer PoC: core.excludesFile = \??\UNC\host\share passed LooksRemote, so `git status` during uninstall
        // planning opened the share through CreateFileW (SMB SynSent, NTLM attempt, 15 s stall).
        var repo = _dir.Sub("nt" + Guid.NewGuid().ToString("N")[..6]);
        var gitDir = _dir.Sub(Path.GetFileName(repo), ".git");
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(gitDir, "config"), "[core]\n\tbare = false\n\t" + line + "\n");
        Assert.False(GitRepoGuard.TryResolveLocalGitDir(repo, out _, out var reason));
        Assert.Contains("network", reason, StringComparison.Ordinal);
        await AssertRefusedWithoutGitAsync(repo);
    }

    [Theory]
    [InlineData(@"\??\UNC\host\share\x", true)]
    [InlineData(@"\??\GLOBALROOT\Device\Mup\host\share\x", true)]
    [InlineData("/??/UNC/host/share/x", true)]
    [InlineData(@"\\?\UNC\host\share", true)]
    [InlineData(@"\\.\pipe\x", true)]
    [InlineData(@"C:\Users\me\.gitignore_global", false)]
    [InlineData("~/.gitignore", false)]
    [InlineData("https://example.com/x.git", false)]
    [InlineData("refs/heads/main", false)]
    [InlineData(":(optional)//host/share/x", true)]
    [InlineData(@":(optional)\\host\share", true)]
    [InlineData(":(optional)%(prefix)//host/share/x", true)]
    [InlineData(" :(optional) %(prefix) :(optional)//host/share/x", true)]
    [InlineData(":(optional)~/.gitignore", false)]
    public void LooksRemote_rejects_nt_and_device_namespaces(string value, bool remote) =>
        Assert.Equal(remote, GitRepoGuard.LooksRemote(value));

    /// <summary>A hand-made repository (HEAD + config) whose config holds <paramref name="configLine"/> under [core].</summary>
    private string RepoWithCoreLine(string configLine)
    {
        var repo = _dir.Sub("pv" + Guid.NewGuid().ToString("N")[..6]);
        var gitDir = _dir.Sub(Path.GetFileName(repo), ".git");
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(gitDir, "config"), "[core]\n\tbare = false\n\t" + configLine + "\n");
        return repo;
    }

    [Theory]
    [InlineData("attributesFile")]
    [InlineData("excludesFile")]
    [InlineData("hooksPath")]
    public async Task Pathname_value_reached_through_a_worktree_junction_is_refused(string key)
    {
        // git resolves a relative pathname value against the worktree top: a "sub" link to a share would make
        // `git status` open it although "sub/x" looks harmless. The junction here targets a local temp folder.
        using var outside = new TempDir();
        outside.File("x", "*.log\n");
        var repo = RepoWithCoreLine($"{key} = sub/x");
        if (!TestTools.TryCreateJunction(Path.Combine(repo, "sub"), outside.Path))
        {
            return; // junctions not supported here: skip
        }

        Assert.False(GitRepoGuard.TryResolveLocalGitDir(repo, out _, out var reason));
        Assert.Contains("link", reason, StringComparison.Ordinal);
        await AssertRefusedWithoutGitAsync(repo);

        // Absolute local spelling of the same path: refused too.
        var absolute = RepoWithCoreLine($"{key} = {Sh(Path.Combine(repo, "sub", "x"))}");
        Assert.False(GitRepoGuard.TryResolveLocalGitDir(absolute, out _, out _));

        // Same value without the link: accepted.
        var plain = RepoWithCoreLine($"{key} = sub/x");
        File.WriteAllText(Path.Combine(_dir.Sub(Path.GetFileName(plain), "sub"), "x"), "*.log\n");
        Assert.True(GitRepoGuard.TryResolveLocalGitDir(plain, out _, out var plainReason), plainReason);
    }

    [Theory]
    [InlineData("excludesFile = %(prefix)/etc/x")] // runtime prefix plus ".." could walk anywhere
    [InlineData("attributesFile = ~otheruser/x")]
    [InlineData("excludesFile = D:x")] // drive-relative
    [InlineData("excludesFile = /x")] // root-relative
    public void Pathname_value_that_cannot_be_resolved_locally_is_refused(string line) =>
        Assert.False(GitRepoGuard.TryResolveLocalGitDir(RepoWithCoreLine(line), out _, out _));

    [Theory]
    [InlineData("excludesFile = .gitignore-extra")]
    [InlineData("attributesFile = ~/.gitattributes")]
    [InlineData("hooksPath = .husky/_")]
    [InlineData("excludesFile =")]
    public void Ordinary_pathname_values_are_accepted(string line) =>
        Assert.True(GitRepoGuard.TryResolveLocalGitDir(RepoWithCoreLine(line), out _, out var reason), reason);

    [Fact]
    public async Task Repository_excludes_and_attributes_files_are_pinned_for_local_git_calls()
    {
        // Second layer behind the guard: git calls on scanned repositories never open the repository's own
        // core.excludesFile / core.attributesFile. The repository's .gitignore still applies.
        var repo = _dir.Sub("pinned");
        if (!TestTools.GitInitWithCommit(repo))
        {
            return;
        }

        File.WriteAllText(Path.Combine(repo, "repo-excludes"), "*.log\n");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "*.tmp\n");
        File.WriteAllText(Path.Combine(repo, "a.log"), "x");
        File.WriteAllText(Path.Combine(repo, "b.tmp"), "x");
        Assert.Equal(0, TestTools.Run("git", repo, "config", "core.excludesFile", "repo-excludes"));
        Assert.Contains("a.log", GitOut(repo, "ls-files", "--others", "--ignored", "--exclude-standard"), StringComparison.Ordinal); // plain git honours it

        var ignored = await new GitClient(NullLogger<GitClient>.Instance).GetIgnoredPathsAsync(repo, CancellationToken.None);
        Assert.Contains("b.tmp", ignored);
        Assert.DoesNotContain("a.log", ignored);
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
