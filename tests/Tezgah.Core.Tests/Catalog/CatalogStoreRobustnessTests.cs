using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Catalog;
using Tezgah.Core.Model;
using Tezgah.Core.Tests.Discovery;

namespace Tezgah.Core.Tests.Catalog;

public sealed class CatalogStoreRobustnessTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string StorePath => Path.Combine(_dir.Path, "catalog.json");

    private static CatalogState Sample(string note) => new()
    {
        Items = [new CatalogItem { Id = "path:c:\\x", Kind = ItemKind.GitRepository, Source = Sources.FileSystem, Name = "x", Path = @"C:\x" }],
        Meta = new Dictionary<string, UserMeta> { ["path:c:\\x"] = new() { Note = note, Trusted = true } },
    };

    [Fact]
    public async Task Locked_catalog_is_not_treated_as_corrupt_and_is_never_overwritten()
    {
        using (var seed = new CatalogStore(StorePath))
        {
            await seed.SaveAsync(Sample("precious"), CancellationToken.None);
        }

        var original = await File.ReadAllBytesAsync(StorePath);
        using var store = new CatalogStore(StorePath);
        using (new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<CatalogUnavailableException>(() => store.LoadAsync(CancellationToken.None));
        }

        Assert.Empty(Directory.GetFiles(_dir.Path, "catalog.json.corrupt-*"));
        Assert.Equal(original, await File.ReadAllBytesAsync(StorePath));
        Assert.Equal("precious", (await store.LoadAsync(CancellationToken.None)).Meta["path:c:\\x"].Note); // released: loads fine
    }

    [Fact]
    public async Task Service_does_not_initialize_on_a_locked_catalog()
    {
        using (var seed = new CatalogStore(StorePath))
        {
            await seed.SaveAsync(Sample("precious"), CancellationToken.None);
        }

        using var store = new CatalogStore(StorePath);
        using var svc = new CatalogService([], store, new FakeGitClientForStore(), DetectorTests.NewInspector(), NullLogger<CatalogService>.Instance);
        using (new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<CatalogUnavailableException>(() => svc.InitializeAsync(CancellationToken.None));
        }

        await svc.UpdateMetaAsync("other", m => m with { Favorite = true }, CancellationToken.None); // loads first, then saves
        var reloaded = await new CatalogStore(Path.Combine(_dir.Path, "catalog.json")).LoadAsync(CancellationToken.None);
        Assert.Equal("precious", reloaded.Meta["path:c:\\x"].Note);
        Assert.True(reloaded.Meta["other"].Favorite);
    }

    private sealed class FakeGitClientForStore : Abstractions.IGitClient
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(false);

        public Task<GitInfo?> GetInfoAsync(string repoPath, bool fetch, CancellationToken ct) => Task.FromResult<GitInfo?>(null);

        public Task<CommandResult> PullAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Null_entries_and_collections_are_repaired()
    {
        await File.WriteAllTextAsync(StorePath, """
            {
              "items": [ { "id": "a", "kind": "GitRepository", "source": "fs", "name": "a", "runProfiles": null, "projectTypes": null, "artifacts": null } ],
              "meta": { "x": null, "y": { "customProfiles": null, "tags": null, "trusted": true } },
              "settings": { "forgottenAfterDays": 99999999, "maxDepth": 100000, "scanRoots": null }
            }
            """);
        using var store = new CatalogStore(StorePath);
        var s = await store.LoadAsync(CancellationToken.None);

        var item = Assert.Single(s.Items);
        Assert.NotNull(item.RunProfiles);
        Assert.NotNull(item.ProjectTypes);
        Assert.False(s.Meta.ContainsKey("x"));
        Assert.NotNull(s.Meta["y"].CustomProfiles);
        Assert.NotNull(s.Meta["y"].Tags);
        Assert.Equal(3650, s.Settings.ForgottenAfterDays);
        Assert.Equal(32, s.Settings.MaxDepth);
        Assert.NotNull(s.Settings.ScanRoots);

        await File.WriteAllTextAsync(Path.Combine(_dir.Path, "c2.json"), File.ReadAllText(StorePath));
        using var svc = new CatalogService([], new CatalogStore(Path.Combine(_dir.Path, "c2.json")), new FakeGitClientForStore(), DetectorTests.NewInspector(), NullLogger<CatalogService>.Instance);
        await svc.InitializeAsync(CancellationToken.None);
        Assert.Empty(svc.GetProfiles("y"));
        Assert.True(svc.GetMeta("y").Trusted);
        Assert.False(svc.GetMeta("x").Trusted);
        Assert.True(svc.IsForgotten(item with { LastActivity = DateTimeOffset.Now.AddDays(-5000) })); // clamped to 3650 days, no overflow
        Assert.False(svc.IsForgotten(item with { LastActivity = DateTimeOffset.Now.AddDays(-3000) }));
    }

    [Fact]
    public async Task Unknown_enum_value_drops_only_that_item()
    {
        await File.WriteAllTextAsync(StorePath, """
            {
              "schemaVersion": 1,
              "items": [
                { "id": "good", "kind": "GitRepository", "source": "fs", "name": "good" },
                { "id": "future", "kind": "Container", "source": "fs", "name": "future" }
              ],
              "meta": { "good": { "note": "keep me", "favorite": true } },
              "settings": { "language": "en" }
            }
            """);
        using var store = new CatalogStore(StorePath);
        var s = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("good", Assert.Single(s.Items).Id);
        Assert.Equal("keep me", s.Meta["good"].Note);
        Assert.Equal("en", s.Settings.Language);
        Assert.Empty(Directory.GetFiles(_dir.Path, "catalog.json.corrupt-*"));
        Assert.Single(Directory.GetFiles(_dir.Path, "catalog.json.partial-*")); // full original kept before any overwrite
    }

    [Fact]
    public async Task Newer_schema_is_loaded_read_only_and_never_overwritten()
    {
        const string json = """{ "schemaVersion": 99, "items": [], "meta": { "a": { "note": "from the future" } } }""";
        await File.WriteAllTextAsync(StorePath, json);
        using var store = new CatalogStore(StorePath);
        var s = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("from the future", s.Meta["a"].Note);
        Assert.True(store.IsReadOnly);

        await store.SaveAsync(new CatalogState(), CancellationToken.None);
        Assert.Equal(json, await File.ReadAllTextAsync(StorePath));
    }

    [Fact]
    public async Task Partial_replace_failure_keeps_the_new_state()
    {
        using var store = new CatalogStore(StorePath);
        await store.SaveAsync(Sample("old"), CancellationToken.None);

        // Simulate ERROR_UNABLE_TO_MOVE_REPLACEMENT_2: the original is already at the backup path, the target is gone.
        File.Move(StorePath, store.BackupPath, overwrite: true);
        var temp = Path.Combine(_dir.Path, ".catalog.json.test.tmp");
        await File.WriteAllTextAsync(temp, """{ "items": [], "meta": { "n": { "note": "new" } } }""");

        store.CommitFallback(temp);

        Assert.Contains("new", await File.ReadAllTextAsync(StorePath), StringComparison.Ordinal);
        Assert.Contains("old", await File.ReadAllTextAsync(store.BackupPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uncommitted_temp_file_is_recovered_when_the_catalog_is_missing()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir.Path, ".catalog.json.abc.tmp"), """{ "items": [], "meta": { "n": { "note": "recovered" } } }""");
        using var store = new CatalogStore(StorePath);
        Assert.Equal("recovered", (await store.LoadAsync(CancellationToken.None)).Meta["n"].Note);
    }

    [Fact]
    public async Task Credentials_in_stored_remote_urls_are_scrubbed_on_load()
    {
        await File.WriteAllTextAsync(StorePath, """
            { "items": [ { "id": "a", "kind": "GitRepository", "source": "fs", "name": "a", "git": { "remoteUrl": "https://john@corp.com:ghp_SECRET@github.com/o/r.git" } } ] }
            """);
        using var store = new CatalogStore(StorePath);
        var s = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("https://github.com/o/r.git", s.Items[0].Git!.RemoteUrl);
    }
}
