using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Catalog;
using Tezgah.Core.Model;
using Tezgah.Core.Tests.Discovery;

namespace Tezgah.Core.Tests.Catalog;

public sealed class CatalogStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string StorePath => Path.Combine(_dir.Path, "sub", "catalog.json");

    private static CatalogState Sample(string name) => new()
    {
        LastScanAt = DateTimeOffset.Now,
        Items =
        [
            new CatalogItem
            {
                Id = "path:c:\\x", Kind = ItemKind.GitRepository, Source = Sources.FileSystem, Name = name, Path = @"C:\x",
                Git = new GitInfo { Branch = "main", Ahead = 1 },
                RunProfiles = [new RunProfile { Id = "npm:dev", Label = "npm run dev", Kind = RunProfileKind.Run, Origin = "node", IsDefault = true,
                    Command = new CommandSpec { FileName = "npm", Arguments = ["run", "dev"], WorkingDirectory = @"C:\x" } }],
                Artifacts = [new ArtifactDir(@"C:\x\node_modules", "node", 42)],
            },
        ],
        Meta = new Dictionary<string, UserMeta> { ["path:c:\\x"] = new() { Favorite = true, Tags = ["a"], Trusted = true } },
        Settings = new TezgahSettings { MaxDepth = 3, Language = "en" },
    };

    [Fact]
    public async Task RoundTripsAndKeepsBackup()
    {
        using var store = new CatalogStore(StorePath);
        await store.SaveAsync(Sample("first"), CancellationToken.None);
        await store.SaveAsync(Sample("second"), CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);
        var item = Assert.Single(loaded.Items);
        Assert.Equal("second", item.Name);
        Assert.Equal(ItemKind.GitRepository, item.Kind);
        Assert.Equal(["run", "dev"], item.RunProfiles[0].Command.Arguments);
        Assert.Equal(42, item.Artifacts[0].SizeBytes);
        Assert.True(loaded.Meta["path:c:\\x"].Favorite);
        Assert.Equal(3, loaded.Settings.MaxDepth);
        Assert.True(File.Exists(store.BackupPath));
        Assert.Contains("first", await File.ReadAllTextAsync(store.BackupPath));

        var json = await File.ReadAllTextAsync(StorePath);
        Assert.Contains("\"kind\": \"GitRepository\"", json, StringComparison.Ordinal); // camelCase + string enums, indented
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StorePath)!, "*.tmp"));
    }

    [Fact]
    public async Task CorruptFileIsMovedAsideAndBackupUsed()
    {
        using var store = new CatalogStore(StorePath);
        await store.SaveAsync(Sample("good"), CancellationToken.None);
        await store.SaveAsync(Sample("good2"), CancellationToken.None);
        await File.WriteAllTextAsync(StorePath, "{ this is not json");

        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("good", Assert.Single(loaded.Items).Name); // from .bak
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StorePath)!, "catalog.json.corrupt-*"));
    }

    [Fact]
    public async Task CorruptWithoutBackupStartsFresh()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        await File.WriteAllTextAsync(StorePath, "[1,2,3]");
        using var store = new CatalogStore(StorePath);
        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Empty(loaded.Items);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public async Task MissingFileGivesEmptyState()
    {
        using var store = new CatalogStore(StorePath);
        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Empty(loaded.Items);
        Assert.Equal(6, loaded.Settings.MaxDepth);
    }

    [Fact]
    public async Task ConcurrentSavesDoNotCorrupt()
    {
        using var store = new CatalogStore(StorePath);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.SaveAsync(Sample("n" + i), CancellationToken.None)));
        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.StartsWith("n", Assert.Single(loaded.Items).Name, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultPathIsUnderLocalAppData() =>
        Assert.EndsWith(@"Tezgah\catalog.json", CatalogStore.DefaultPath, StringComparison.OrdinalIgnoreCase);
}

public class CloneUrlValidatorTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("ssh://git@github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("git@gitlab.com:group/sub/repo")]
    public void AcceptsGitUrls(string url) => Assert.True(CloneUrlValidator.IsValid(url, out _));

    [Theory]
    [InlineData("--upload-pack=touch /tmp/pwned")]
    [InlineData("-u calc")]
    [InlineData("https://github.com/x/y --upload-pack=calc")]
    [InlineData("ext::sh -c touch% /tmp/pwned")]
    [InlineData("ext::calc")]
    [InlineData("file:///C:/Windows")]
    [InlineData("file::C:/x")]
    [InlineData("fd::17")]
    [InlineData("C:\\repos\\local")]
    [InlineData("\\\\server\\share\\repo")]
    [InlineData("http://github.com/x/y")]
    [InlineData("git://github.com/x/y")]
    [InlineData("https://user:token@github.com/x/y")]
    [InlineData("https://github.com/")]
    [InlineData("ssh://-oProxyCommand=calc/x")]
    [InlineData("git@github.com:-x/y")]
    [InlineData("")]
    [InlineData("https://github.com/x/y\nhttps://evil")]
    public void RejectsDangerousUrls(string url)
    {
        Assert.False(CloneUrlValidator.IsValid(url, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("https://github.com/owner/My.Repo.git", "My.Repo")]
    [InlineData("git@github.com:owner/repo", "repo")]
    [InlineData("https://github.com/owner/repo/", "repo")]
    public void FolderName(string url, string expected) => Assert.Equal(expected, CloneUrlValidator.FolderNameFor(url));
}

public sealed class CatalogServiceTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class FakeProvider(string id, Func<IReadOnlyList<CatalogItem>> items, bool available = true) : IDiscoveryProvider
    {
        public string Id => id;

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(available);

        public Task<IReadOnlyList<CatalogItem>> DiscoverAsync(TezgahSettings settings, IProgress<ScanProgress>? progress, CancellationToken ct) => Task.FromResult(items());
    }

    private sealed class ThrowingProvider(string id) : IDiscoveryProvider
    {
        public string Id => id;

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyList<CatalogItem>> DiscoverAsync(TezgahSettings settings, IProgress<ScanProgress>? progress, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private static CatalogItem Item(string id, string source, string name, DateTimeOffset? last = null, params RunProfile[] profiles) => new()
    {
        Id = id, Kind = source == Sources.FileSystem ? ItemKind.GitRepository : ItemKind.PackageTool, Source = source, Name = name,
        LastActivity = last, RunProfiles = profiles,
    };

    private static RunProfile Profile(string id, bool isDefault = false) => new()
    {
        Id = id, Label = id, Kind = RunProfileKind.Run, Origin = "test", IsDefault = isDefault,
        Command = new CommandSpec { FileName = "x", WorkingDirectory = "C:\\" },
    };

    private static CatalogService NewService(CatalogStore store, params IDiscoveryProvider[] providers) =>
        new(providers, store, new Tezgah.Core.Discovery.GitClient(NullLogger<Tezgah.Core.Discovery.GitClient>.Instance), DetectorTests.NewInspector(), NullLogger<CatalogService>.Instance);

    [Fact]
    public async Task ScanMergesProvidersPreservesMetaAndPersists()
    {
        var storePath = Path.Combine(_dir.Path, "catalog.json");
        using var store = new CatalogStore(storePath);
        var npmItems = new List<CatalogItem> { Item("npm:a", Sources.NpmGlobal, "a"), Item("npm:b", Sources.NpmGlobal, "b") };
        using var svc = NewService(store,
            new FakeProvider(Sources.NpmGlobal, () => npmItems.ToList()),
            new FakeProvider(Sources.Cargo, () => [Item("cargo:c", Sources.Cargo, "c")]));
        await svc.InitializeAsync(CancellationToken.None);
        var changes = 0;
        svc.Changed += (_, _) => Interlocked.Increment(ref changes);

        await svc.ScanAsync(null, CancellationToken.None);
        Assert.Equal(["a", "b", "c"], svc.State.Items.Select(i => i.Name));
        Assert.NotNull(svc.State.LastScanAt);
        Assert.True(changes > 0);

        var lastRun = DateTimeOffset.Now;
        await svc.UpdateMetaAsync("npm:a", m => m with { Favorite = true, Note = "keep", LastRunAt = lastRun }, CancellationToken.None);
        npmItems.RemoveAt(1);
        await svc.ScanAsync(null, CancellationToken.None);

        Assert.Equal(["a", "c"], svc.State.Items.Select(i => i.Name));
        Assert.Equal("keep", svc.GetMeta("npm:a").Note);
        Assert.Equal(lastRun, svc.State.Items.Single(i => i.Id == "npm:a").LastActivity); // meta LastRunAt counts as activity

        // Reload from disk with a fresh service.
        using var store2 = new CatalogStore(storePath);
        using var svc2 = NewService(store2);
        await svc2.InitializeAsync(CancellationToken.None);
        Assert.Equal(2, svc2.State.Items.Count);
        Assert.True(svc2.GetMeta("npm:a").Favorite);
    }

    [Fact]
    public async Task FailedProviderKeepsPreviousItemsAndDisabledProvidersAreDropped()
    {
        using var store = new CatalogStore(Path.Combine(_dir.Path, "c.json"));
        await store.SaveAsync(new CatalogState
        {
            Items = [Item("cargo:old", Sources.Cargo, "old"), Item("registry:x", Sources.Registry, "x")],
            Settings = new TezgahSettings { IncludeInstalledApps = false },
        }, CancellationToken.None);
        using var svc = NewService(store, new ThrowingProvider(Sources.Cargo), new FakeProvider(Sources.Registry, () => [Item("registry:y", Sources.Registry, "y")]));
        await svc.ScanAsync(null, CancellationToken.None);
        Assert.Equal(["old"], svc.State.Items.Select(i => i.Name)); // cargo kept, registry disabled -> removed and not scanned
    }

    [Fact]
    public async Task UnavailableProviderClearsItsItems()
    {
        using var store = new CatalogStore(Path.Combine(_dir.Path, "c.json"));
        await store.SaveAsync(new CatalogState { Items = [Item("pipx:gone", Sources.Pipx, "gone")] }, CancellationToken.None);
        using var svc = NewService(store, new FakeProvider(Sources.Pipx, () => [], available: false));
        await svc.ScanAsync(null, CancellationToken.None);
        Assert.Empty(svc.State.Items);
    }

    [Fact]
    public async Task ProfilesHonourUserDefaultAndCustomProfiles()
    {
        using var store = new CatalogStore(Path.Combine(_dir.Path, "c.json"));
        using var svc = NewService(store, new FakeProvider(Sources.Cargo, () => [Item("cargo:z", Sources.Cargo, "z", null, Profile("a", true), Profile("b"))]));
        await svc.ScanAsync(null, CancellationToken.None);

        Assert.Equal("a", Assert.Single(svc.GetProfiles("cargo:z"), p => p.IsDefault).Id);

        await svc.UpdateMetaAsync("cargo:z", m => m with { CustomProfiles = [Profile("mine")], DefaultProfileId = "mine" }, CancellationToken.None);
        var profiles = svc.GetProfiles("cargo:z");
        Assert.Equal(["a", "b", "mine"], profiles.Select(p => p.Id));
        Assert.Equal("mine", Assert.Single(profiles, p => p.IsDefault).Id);
    }

    [Fact]
    public async Task ForgottenLogic()
    {
        using var store = new CatalogStore(Path.Combine(_dir.Path, "c.json"));
        using var svc = NewService(store);
        await svc.InitializeAsync(CancellationToken.None);
        var old = Item("x", Sources.Cargo, "x", DateTimeOffset.Now.AddDays(-200));
        var recent = Item("y", Sources.Cargo, "y", DateTimeOffset.Now.AddDays(-5));
        Assert.True(svc.IsForgotten(old));
        Assert.False(svc.IsForgotten(recent));
        await svc.UpdateMetaAsync("x", m => m with { Favorite = true }, CancellationToken.None);
        Assert.False(svc.IsForgotten(old));
    }

    [Fact]
    public async Task AddFolderAndRemove()
    {
        using var store = new CatalogStore(Path.Combine(_dir.Path, "c.json"));
        using var svc = NewService(store);
        var project = _dir.Sub("proj");
        File.WriteAllText(Path.Combine(project, "Cargo.toml"), "[package]\nname=\"p\"\n");
        var item = await svc.AddFolderAsync(project, CancellationToken.None);
        Assert.NotNull(item);
        Assert.Equal(ItemKind.ProjectFolder, item!.Kind);
        Assert.Contains("rust", item.ProjectTypes);
        Assert.Single(svc.State.Items);

        var empty = _dir.Sub("empty"); // user picked it explicitly: still added
        Assert.NotNull(await svc.AddFolderAsync(empty, CancellationToken.None));
        Assert.Null(await svc.AddFolderAsync(Path.Combine(_dir.Path, "missing"), CancellationToken.None));

        await svc.UpdateMetaAsync(item.Id, m => m with { Note = "n" }, CancellationToken.None);
        await svc.RemoveItemAsync(item.Id, CancellationToken.None);
        Assert.DoesNotContain(svc.State.Items, i => i.Id == item.Id);
        Assert.Null(svc.GetMeta(item.Id).Note);
    }

    [Fact]
    public async Task ManuallyAddedFolderSurvivesRescan()
    {
        using var store = new CatalogStore(Path.Combine(_dir.Path, "c.json"));
        using var svc = NewService(store, new FakeProvider(Sources.FileSystem, () => []));
        var project = _dir.Sub("manual");
        File.WriteAllText(Path.Combine(project, "go.mod"), "module m");
        await svc.AddFolderAsync(project, CancellationToken.None);
        await svc.ScanAsync(null, CancellationToken.None);
        Assert.Single(svc.State.Items);

        Directory.Delete(project, true);
        await svc.ScanAsync(null, CancellationToken.None);
        Assert.Empty(svc.State.Items);
    }

    [Fact]
    public async Task CloneRejectsInjection()
    {
        using var store = new CatalogStore(Path.Combine(_dir.Path, "c.json"));
        using var svc = NewService(store);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.CloneAsync("--upload-pack=calc", _dir.Path, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.CloneAsync("ext::sh -c calc", _dir.Path, null, CancellationToken.None));
        Assert.Empty(Directory.GetDirectories(_dir.Path));
    }

    [Fact]
    public async Task CloneFromLocalBareRepoIsRefusedButHttpsShapeWouldPass()
    {
        // Local paths are never accepted, even if they are valid git repositories.
        using var store = new CatalogStore(Path.Combine(_dir.Path, "c.json"));
        using var svc = NewService(store);
        var src = _dir.Sub("src");
        if (!TestTools.GitInitWithCommit(src))
        {
            return;
        }

        await Assert.ThrowsAsync<ArgumentException>(() => svc.CloneAsync(src, _dir.Sub("dest"), null, CancellationToken.None));
    }

    [Fact]
    public void DiRegistrationResolvesEverything()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddTezgahCore();
        using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        Assert.NotNull(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ICatalogService>(sp));
        Assert.True(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetServices<IDiscoveryProvider>(sp).Count() >= 8);
        Assert.True(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetServices<IProjectDetector>(sp).Count() >= 9);
    }
}
