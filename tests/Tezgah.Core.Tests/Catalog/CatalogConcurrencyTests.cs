using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Catalog;
using Tezgah.Core.Model;
using Tezgah.Core.Tests.Discovery;

namespace Tezgah.Core.Tests.Catalog;

/// <summary>User actions taken while a scan runs must survive the scan's merge.</summary>
public sealed class CatalogConcurrencyTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class BlockingProvider(string id, Func<IReadOnlyList<CatalogItem>> items) : IDiscoveryProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => id;

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public async Task<IReadOnlyList<CatalogItem>> DiscoverAsync(TezgahSettings settings, IProgress<ScanProgress>? progress, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return items();
        }
    }

    private CatalogService Service(params IDiscoveryProvider[] providers) =>
        new(providers, new CatalogStore(Path.Combine(_dir.Path, "c.json")), new CatalogTrustTests.FetchRecordingGit(),
            DetectorTests.NewInspector(new CatalogTrustTests.FetchRecordingGit()), NullLogger<CatalogService>.Instance);

    private static CatalogItem Tool(string id) => new() { Id = id, Kind = ItemKind.PackageTool, Source = Sources.NpmGlobal, Name = id };

    [Fact]
    public async Task Folder_added_during_a_scan_survives_it()
    {
        var fs = new BlockingProvider(Sources.FileSystem, () => []);
        using var svc = Service(fs);
        await svc.InitializeAsync(CancellationToken.None);
        var scan = svc.ScanAsync(null, CancellationToken.None);
        await fs.Entered.Task;

        var project = _dir.Sub("cloned");
        File.WriteAllText(Path.Combine(project, "go.mod"), "module m");
        var added = await svc.AddFolderAsync(project, CancellationToken.None);
        fs.Release.SetResult();
        await scan;

        Assert.Contains(svc.State.Items, i => i.Id == added!.Id);
    }

    [Fact]
    public async Task Item_removed_during_a_scan_is_not_resurrected()
    {
        var npm = new BlockingProvider(Sources.NpmGlobal, () => [Tool("npm:x"), Tool("npm:y")]);
        using var svc = Service(npm);
        npm.Release.SetResult();
        await svc.ScanAsync(null, CancellationToken.None);
        Assert.Equal(2, svc.State.Items.Count);

        var again = new BlockingProvider(Sources.NpmGlobal, () => [Tool("npm:x"), Tool("npm:y")]);
        using var svc2 = new CatalogService([again], new CatalogStore(Path.Combine(_dir.Path, "c.json")), new CatalogTrustTests.FetchRecordingGit(),
            DetectorTests.NewInspector(), NullLogger<CatalogService>.Instance);
        await svc2.InitializeAsync(CancellationToken.None);
        var scan = svc2.ScanAsync(null, CancellationToken.None);
        await again.Entered.Task;
        await svc2.RemoveItemAsync("npm:x", CancellationToken.None); // e.g. the uninstall's last step
        again.Release.SetResult();
        await scan;

        Assert.DoesNotContain(svc2.State.Items, i => i.Id == "npm:x");
        Assert.Contains(svc2.State.Items, i => i.Id == "npm:y");
    }

    [Fact]
    public async Task Refresh_does_not_resurrect_an_item_removed_meanwhile()
    {
        var gate = new BlockingProvider(Sources.NpmGlobal, () => [Tool("npm:x")]);
        gate.Release.SetResult();
        using var svc = Service(gate);
        await svc.ScanAsync(null, CancellationToken.None);

        var slow = new BlockingProvider(Sources.NpmGlobal, () => [Tool("npm:x")]);
        using var svc2 = new CatalogService([slow], new CatalogStore(Path.Combine(_dir.Path, "c.json")), new CatalogTrustTests.FetchRecordingGit(),
            DetectorTests.NewInspector(), NullLogger<CatalogService>.Instance);
        await svc2.InitializeAsync(CancellationToken.None);
        var refresh = svc2.RefreshItemAsync("npm:x", fetchGit: false, CancellationToken.None);
        await slow.Entered.Task;
        await svc2.RemoveItemAsync("npm:x", CancellationToken.None);
        slow.Release.SetResult();
        await refresh;

        Assert.Empty(svc2.State.Items);
    }
}
