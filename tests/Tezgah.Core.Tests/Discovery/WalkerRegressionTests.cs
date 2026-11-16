using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Discovery;
using Tezgah.Core.Model;

namespace Tezgah.Core.Tests.Discovery;

public sealed class WalkerRegressionTests : IDisposable
{
    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    private static FileSystemProvider NewProvider() => new(DetectorTests.NewInspector(), NullLogger<FileSystemProvider>.Instance);

    private TezgahSettings Settings() => new() { ScanRoots = [_root.Path], MaxDepth = 8 };

    private List<string> Rel(IEnumerable<string> paths) => paths.Select(p => Path.GetRelativePath(_root.Path, p)).ToList();

    [Fact]
    public async Task Repositories_under_a_workspace_project_are_found()
    {
        _root.File(@"work\shop\docker-compose.yml", "services: {}");
        _root.File(@"work\shop\api\.git\HEAD", "ref: refs/heads/main");
        _root.File(@"work\shop\web\.git\HEAD", "ref: refs/heads/main");
        _root.File(@"work\shop\libs\worker\.git\HEAD", "ref: refs/heads/main");
        _root.File(@"work\shop\web\packages\x\package.json", "{}"); // inside a repo: not a separate project
        _root.File(@"sln-root\X.sln", "");
        _root.File(@"sln-root\lib\package.json", "{}"); // sub-package of a project, not a repo: still hidden

        var found = Rel(await NewProvider().FindCandidatesAsync([_root.Path], Settings(), null, CancellationToken.None));

        Assert.Equal([@"sln-root", @"work\shop", @"work\shop\api", @"work\shop\libs\worker", @"work\shop\web"], found);
    }

    [Fact]
    public async Task Direct_child_repositories_of_a_repository_are_found()
    {
        _root.File(@"notes\.git\HEAD", "ref: refs/heads/main");
        _root.File(@"notes\clone\.git\HEAD", "ref: refs/heads/main");
        _root.File(@"notes\deep\deeper\x\.git\HEAD", "ref: refs/heads/main");

        var found = Rel(await NewProvider().FindCandidatesAsync([_root.Path], Settings(), null, CancellationToken.None));

        Assert.Equal([@"notes", @"notes\clone"], found);
    }

    [Fact]
    public async Task Tool_caches_are_not_projects_but_gopath_src_is()
    {
        var home = _root.Path;
        _root.File(@"go\pkg\mod\github.com\o\r@v1.0.0\go.mod", "module r");
        _root.File(@"scoop\buckets\main\.git\HEAD", "ref: refs/heads/master");
        _root.File(@"miniconda3\pkgs\x\setup.py", "");
        _root.File(@"go\src\myproj\go.mod", "module myproj");
        _root.File(@"projects\pkg\x\package.json", "{}");
        var excluded = FileSystemProvider.BuildToolCachePaths(home, _ => null);

        var found = Rel(await NewProvider().FindCandidatesAsync([_root.Path], Settings(), null, CancellationToken.None, excluded));

        Assert.Equal([@"go\src\myproj", @"projects\pkg\x"], found);
    }

    [Fact]
    public void Tool_cache_paths_honour_environment_overrides()
    {
        var set = FileSystemProvider.BuildToolCachePaths(@"C:\Users\u", n => n switch
        {
            "GOPATH" => @"D:\gopath;E:\g2",
            "SCOOP" => @"D:\scoop",
            _ => null,
        });
        Assert.Contains(@"D:\gopath\pkg", set);
        Assert.Contains(@"E:\g2\pkg", set);
        Assert.Contains(@"D:\scoop\buckets", set);
        Assert.DoesNotContain(@"C:\Users\u\go\pkg", set);
    }
}
