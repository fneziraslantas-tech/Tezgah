using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Discovery;
using Tezgah.Core.Model;

namespace Tezgah.Core.Tests.Discovery;

public sealed class FileSystemProviderTests : IDisposable
{
    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    private static FileSystemProvider NewProvider() =>
        new(DetectorTests.NewInspector(), NullLogger<FileSystemProvider>.Instance);

    private TezgahSettings Settings(int maxDepth = 6) => new() { ScanRoots = [_root.Path], MaxDepth = maxDepth };

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path);

    [Fact]
    public async Task FindsProjectsSkipsExcludedAndStopsAtProjects()
    {
        _root.File(@"projects\web\package.json", "{}");
        _root.File(@"projects\web\packages\inner\package.json", "{}");     // below a project: not descended
        _root.File(@"projects\node_modules\lib\package.json", "{}");       // excluded dir
        _root.File(@"projects\bin\x\Cargo.toml", "[package]");           // excluded dir
        _root.File(@"tools\api\pyproject.toml", "[project]");
        _root.File(@"notes\todo.txt", "nothing here");

        var candidates = await NewProvider().FindCandidatesAsync([_root.Path], Settings(), null, CancellationToken.None);
        Assert.Equal([@"projects\web", @"tools\api"], candidates.Select(c => Rel(_root.Path, c)));
    }

    [Fact]
    public async Task RespectsMaxDepth()
    {
        _root.File(@"a\b\c\d\proj\go.mod", "module x");
        Assert.Empty(await NewProvider().FindCandidatesAsync([_root.Path], Settings(maxDepth: 3), null, CancellationToken.None));
        Assert.Single(await NewProvider().FindCandidatesAsync([_root.Path], Settings(maxDepth: 5), null, CancellationToken.None));
    }

    [Fact]
    public async Task WeakMarkersDoNotStopDescent()
    {
        _root.File(@"site\index.html", "<html/>");
        _root.File(@"site\sub\app\package.json", "{}");
        var candidates = await NewProvider().FindCandidatesAsync([_root.Path], Settings(), null, CancellationToken.None);
        Assert.Equal([@"site", @"site\sub\app"], candidates.Select(c => Rel(_root.Path, c)));
    }

    [Fact]
    public async Task GitFileOrDirectoryMarksRepositoryAndStops()
    {
        _root.File(@"repo1\.git\HEAD", "ref: refs/heads/main");
        _root.File(@"repo1\nested\package.json", "{}");
        _root.File(@"wt\.git", "gitdir: C:/elsewhere/.git/worktrees/wt"); // worktree: .git is a file
        var candidates = await NewProvider().FindCandidatesAsync([_root.Path], Settings(), null, CancellationToken.None);
        Assert.Equal(["repo1", "wt"], candidates.Select(c => Rel(_root.Path, c)));
    }

    [Fact]
    public async Task JunctionsAreNotFollowed()
    {
        using var outside = new TempDir();
        outside.File(@"secret-project\package.json", "{}");
        var link = Path.Combine(_root.Path, "link");
        if (!TestTools.TryCreateJunction(link, outside.Path))
        {
            return; // junctions not supported here: skip
        }

        _root.File(@"real\package.json", "{}");
        var candidates = await NewProvider().FindCandidatesAsync([_root.Path], Settings(), null, CancellationToken.None);
        Assert.Equal(["real"], candidates.Select(c => Rel(_root.Path, c)));
    }

    [Fact]
    public async Task NestedRootIsWalkedOnlyOnce()
    {
        var inner = _root.Sub("deep", "inner");
        _root.File(@"deep\inner\x\package.json", "{}");
        var candidates = await NewProvider().FindCandidatesAsync([_root.Path, inner], Settings(), null, CancellationToken.None);
        Assert.Single(candidates);
    }

    [Fact]
    public async Task CancellationIsHonoured()
    {
        _root.File(@"a\package.json", "{}");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewProvider().FindCandidatesAsync([_root.Path], Settings(), null, cts.Token));
    }

    [Fact]
    public async Task DiscoverBuildsItemsWithGitInfo()
    {
        var repo = _root.Sub("myrepo");
        if (!TestTools.GitInitWithCommit(repo, "package.json", """{ "description": "Demo", "scripts": { "dev": "vite" } }"""))
        {
            return; // git not available
        }

        TestTools.Run("git", repo, "remote", "add", "origin", "git@github.com:example/myrepo.git");
        _root.File(@"plain\requirements.txt", "requests");
        _root.File(@"plain\main.py", "print(1)");

        var items = await NewProvider().DiscoverAsync(Settings(), null, CancellationToken.None);
        var r = Assert.Single(items, i => i.Name == "myrepo");
        Assert.Equal(ItemKind.GitRepository, r.Kind);
        Assert.Equal(Sources.FileSystem, r.Source);
        Assert.Equal(ItemIds.ForPath(repo), r.Id);
        Assert.Equal("https://github.com/example/myrepo", r.WebUrl);
        Assert.Equal("Demo", r.Description);
        Assert.Contains("node", r.ProjectTypes);
        Assert.NotNull(r.Git?.LastCommitAt);
        Assert.True(r.SizeBytes > 0);
        Assert.NotNull(r.LastActivity);

        var p = Assert.Single(items, i => i.Name == "plain");
        Assert.Equal(ItemKind.ProjectFolder, p.Kind);
        Assert.Null(p.Git);
        Assert.Contains(p.RunProfiles, x => x.IsDefault);
    }
}
