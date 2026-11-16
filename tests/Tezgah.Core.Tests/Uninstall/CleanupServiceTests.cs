using Tezgah.Core.Model;
using Tezgah.Core.Safety;
using Tezgah.Core.Tests.Execution;
using Tezgah.Core.Uninstall;

namespace Tezgah.Core.Tests.Uninstall;

public sealed class CleanupServiceTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private (CleanupService Service, FakeCatalogService Catalog, CatalogItem Item) Create(params ArtifactDir[] artifacts)
    {
        var path = _dir.Sub("proj");
        var item = new CatalogItem
        {
            Id = ItemIds.ForPath(path),
            Kind = ItemKind.GitRepository,
            Source = Sources.FileSystem,
            Name = "proj",
            Path = path,
            Artifacts = artifacts,
        };
        var catalog = new FakeCatalogService(item);
        var options = new UninstallOptions { AuditLogPath = Path.Combine(_dir.Path, "audit.log") };
        return (new CleanupService(catalog, PathGuard.Default, new FileRemover(PathGuard.Default), options), catalog, item);
    }

    [Fact]
    public async Task Deletes_artifacts_and_returns_bytes_freed()
    {
        var nodeModules = _dir.Sub("proj", "node_modules", "left-pad");
        File.WriteAllBytes(Path.Combine(nodeModules, "index.js"), new byte[1000]);
        var bin = _dir.Sub("proj", "bin");
        File.WriteAllBytes(Path.Combine(bin, "a.dll"), new byte[500]);
        var nm = new ArtifactDir(Path.Combine(_dir.Path, "proj", "node_modules"), "node_modules", 1000);
        var binArtifact = new ArtifactDir(bin, "dotnet", 500);
        var (service, catalog, item) = Create(nm, binArtifact);

        var freed = await service.CleanArtifactsAsync(item.Id, [nm, binArtifact], CancellationToken.None);

        Assert.Equal(1500, freed);
        Assert.False(Directory.Exists(nm.Path));
        Assert.False(Directory.Exists(bin));
        Assert.True(Directory.Exists(item.Path));
        Assert.Equal([item.Id], catalog.Refreshed);
        Assert.Contains("cleanup", File.ReadAllText(Path.Combine(_dir.Path, "audit.log")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_git_folder()
    {
        var git = new ArtifactDir(_dir.Sub("proj", ".git"), "git", 0);
        var (service, _, item) = Create(git);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CleanArtifactsAsync(item.Id, [git], CancellationToken.None));
        Assert.True(Directory.Exists(git.Path));
    }

    [Fact]
    public async Task Refuses_folders_outside_the_item()
    {
        var outside = new ArtifactDir(_dir.Sub("other", "node_modules"), "node_modules", 0);
        var sneaky = new ArtifactDir(Path.Combine(_dir.Path, "proj", "..", "other", "node_modules"), "node_modules", 0);
        var self = new ArtifactDir(_dir.Sub("proj"), "x", 0);
        var (service, _, item) = Create(outside, sneaky, self);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CleanArtifactsAsync(item.Id, [outside], CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CleanArtifactsAsync(item.Id, [sneaky], CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CleanArtifactsAsync(item.Id, [self], CancellationToken.None));
        Assert.True(Directory.Exists(outside.Path));
    }

    [Fact]
    public async Task Refuses_folders_not_detected_for_the_item()
    {
        var src = new ArtifactDir(_dir.Sub("proj", "src"), "node_modules", 0);
        var (service, _, item) = Create();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CleanArtifactsAsync(item.Id, [src], CancellationToken.None));
        Assert.True(Directory.Exists(src.Path));
    }

    [Fact]
    public async Task Refuses_artifact_that_is_a_junction()
    {
        var precious = _dir.Sub("precious");
        File.WriteAllText(Path.Combine(precious, "keep.txt"), "keep");
        _dir.Sub("proj");
        var link = Path.Combine(_dir.Path, "proj", "node_modules");
        if (!Junction.TryCreate(link, precious))
        {
            return;
        }

        var artifact = new ArtifactDir(link, "node_modules", 0);
        var (service, _, item) = Create(artifact);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CleanArtifactsAsync(item.Id, [artifact], CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(precious, "keep.txt")));
    }

    [Fact]
    public async Task Validation_happens_before_any_deletion()
    {
        var good = new ArtifactDir(_dir.Sub("proj", "dist"), "dist", 0);
        var bad = new ArtifactDir(_dir.Sub("proj", ".git", "objects"), "git", 0);
        var (service, _, item) = Create(good, bad);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CleanArtifactsAsync(item.Id, [good, bad], CancellationToken.None));
        Assert.True(Directory.Exists(good.Path));
    }
}
