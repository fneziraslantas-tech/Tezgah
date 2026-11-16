using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Discovery;
using Tezgah.Core.Discovery.Detectors;
using Tezgah.Core.Model;
using Tezgah.Core.Safety;
using Tezgah.Core.Tests.Execution;
using Tezgah.Core.Uninstall;

namespace Tezgah.Core.Tests.Uninstall;

/// <summary>build/dist folders often hold hand-made or committed files.</summary>
public sealed class AmbiguousArtifactTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static int Git(string dir, params string[] args) =>
        Discovery.TestTools.Run("git", dir, ["-c", "user.name=T", "-c", "user.email=t@e.com", "-c", "commit.gpgsign=false", .. args]);

    private (CleanupService Service, CatalogItem Item) Create(string root, params ArtifactDir[] artifacts)
    {
        var item = new CatalogItem
        {
            Id = ItemIds.ForPath(root), Kind = ItemKind.GitRepository, Source = Sources.FileSystem, Name = "p", Path = root, Artifacts = artifacts,
        };
        var options = new UninstallOptions { AuditLogPath = Path.Combine(_dir.Path, "audit.log") };
        return (new CleanupService(new FakeCatalogService(item), PathGuard.Default, new FileRemover(PathGuard.Default), options, new GitClient(NullLogger<GitClient>.Instance)), item);
    }

    private string Repo()
    {
        var root = _dir.Sub("electron-app");
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "a" }""");
        return Discovery.TestTools.GitInitWithCommit(root) ? root : string.Empty;
    }

    [Fact]
    public async Task Build_folder_with_tracked_files_is_refused_and_nothing_is_deleted()
    {
        var root = Repo();
        if (root.Length == 0)
        {
            return;
        }

        var build = _dir.Sub("electron-app", "build");
        File.WriteAllText(Path.Combine(build, "icon.ico"), "icon");
        Assert.Equal(0, Git(root, "add", "-A"));
        Assert.Equal(0, Git(root, "commit", "-q", "-m", "icons"));
        var nm = _dir.Sub("electron-app", "node_modules");
        var a1 = new ArtifactDir(nm, "node", 0);
        var a2 = new ArtifactDir(build, "node", 0, Ambiguous: true);
        var (service, item) = Create(root, a1, a2);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CleanArtifactsAsync(item.Id, [a1, a2], CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(build, "icon.ico")));
        Assert.True(Directory.Exists(nm)); // validation happens before any deletion
    }

    [Fact]
    public async Task Untracked_but_not_ignored_file_in_dist_is_refused()
    {
        var root = Repo();
        if (root.Length == 0)
        {
            return;
        }

        var dist = _dir.Sub("electron-app", "dist");
        File.WriteAllText(Path.Combine(dist, "signing.json"), "{}");
        var a = new ArtifactDir(dist, "node", 0, Ambiguous: true);
        var (service, item) = Create(root, a);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CleanArtifactsAsync(item.Id, [a], CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(dist, "signing.json")));
    }

    [Fact]
    public async Task Ignored_only_dist_is_cleaned()
    {
        var root = Repo();
        if (root.Length == 0)
        {
            return;
        }

        File.WriteAllText(Path.Combine(root, ".gitignore"), "dist/\n");
        Assert.Equal(0, Git(root, "add", "-A"));
        Assert.Equal(0, Git(root, "commit", "-q", "-m", "ignore"));
        var dist = _dir.Sub("electron-app", "dist");
        File.WriteAllText(Path.Combine(dist, "bundle.js"), "x");
        var a = new ArtifactDir(dist, "node", 0, Ambiguous: true);
        var (service, item) = Create(root, a);

        await service.CleanArtifactsAsync(item.Id, [a], CancellationToken.None);
        Assert.False(Directory.Exists(dist));
    }

    [Fact]
    public void Detectors_mark_build_and_dist_ambiguous_but_not_node_modules()
    {
        var root = _dir.Sub("n");
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "n" }""");
        Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        Directory.CreateDirectory(Path.Combine(root, "build"));
        Directory.CreateDirectory(Path.Combine(root, "dist"));
        var artifacts = new NodeDetector().Detect(root)!.Artifacts;
        Assert.False(artifacts.Single(a => a.Path.EndsWith("node_modules", StringComparison.Ordinal)).Ambiguous);
        Assert.True(artifacts.Single(a => a.Path.EndsWith("build", StringComparison.Ordinal)).Ambiguous);
        Assert.True(artifacts.Single(a => a.Path.EndsWith("dist", StringComparison.Ordinal)).Ambiguous);
    }
}
