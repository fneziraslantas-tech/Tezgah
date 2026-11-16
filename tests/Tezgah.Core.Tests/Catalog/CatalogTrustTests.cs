using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Catalog;
using Tezgah.Core.Discovery;
using Tezgah.Core.Model;
using Tezgah.Core.Tests.Discovery;

namespace Tezgah.Core.Tests.Catalog;

/// <summary>Workspace trust is bound to the folder that was trusted, never to a path that later holds different code.</summary>
public sealed class CatalogTrustTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class EmptyFsProvider : IDiscoveryProvider
    {
        public string Id => Sources.FileSystem;

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyList<CatalogItem>> DiscoverAsync(TezgahSettings settings, IProgress<ScanProgress>? progress, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CatalogItem>>([]);
    }

    internal sealed class FetchRecordingGit : IGitClient
    {
        public List<bool> Fetches { get; } = [];

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<GitInfo?> GetInfoAsync(string repoPath, bool fetch, CancellationToken ct)
        {
            Fetches.Add(fetch);
            return Task.FromResult<GitInfo?>(new GitInfo { Branch = "main" });
        }

        public Task<CommandResult> PullAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();
    }

    private CatalogService NewService(IGitClient? git = null) =>
        new([new EmptyFsProvider()], new CatalogStore(Path.Combine(_dir.Path, "c.json")), git ?? new FetchRecordingGit(),
            DetectorTests.NewInspector(git ?? new FetchRecordingGit()), NullLogger<CatalogService>.Instance);

    private string Project(string name)
    {
        var p = _dir.Sub(name);
        File.WriteAllText(Path.Combine(p, "go.mod"), "module m");
        return p;
    }

    [Fact]
    public async Task Deleted_then_recreated_folder_does_not_inherit_trust()
    {
        using var svc = NewService();
        var path = Project("app");
        var item = (await svc.AddFolderAsync(path, CancellationToken.None))!;
        await svc.UpdateMetaAsync(item.Id, m => m with { Trusted = true, Favorite = true }, CancellationToken.None);
        Assert.True(svc.GetMeta(item.Id).Trusted);

        Directory.Delete(path, true);
        await svc.ScanAsync(null, CancellationToken.None);
        Assert.False(svc.GetMeta(item.Id).Trusted);
        Assert.True(svc.GetMeta(item.Id).Favorite); // user data other than trust is kept

        Project("app");
        var again = (await svc.AddFolderAsync(path, CancellationToken.None))!;
        Assert.Equal(item.Id, again.Id);
        Assert.False(svc.GetMeta(item.Id).Trusted);
    }

    [Fact]
    public async Task Stale_meta_never_trusts_a_newly_added_folder()
    {
        using var svc = NewService();
        var path = Project("other");
        var id = ItemIds.ForPath(path);
        await svc.UpdateMetaAsync(id, m => m with { Trusted = true }, CancellationToken.None); // orphaned trust, no item yet

        await svc.AddFolderAsync(path, CancellationToken.None);

        Assert.False(svc.GetMeta(id).Trusted);
    }

    [Fact]
    public async Task Folder_replaced_while_untracked_loses_trust_on_rescan()
    {
        using var svc = NewService();
        var path = Project("swap");
        var item = (await svc.AddFolderAsync(path, CancellationToken.None))!;
        await svc.UpdateMetaAsync(item.Id, m => m with { Trusted = true }, CancellationToken.None);
        await svc.ScanAsync(null, CancellationToken.None);
        Assert.True(svc.GetMeta(item.Id).Trusted); // same folder: trust survives a rescan

        Directory.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-3)); // a different folder now lives at the path
        await svc.ScanAsync(null, CancellationToken.None);

        Assert.False(svc.GetMeta(item.Id).Trusted);
    }

    [Fact]
    public async Task Folder_replaced_after_the_scan_is_untrusted_at_point_of_use()
    {
        using var svc = NewService();
        var path = Project("swapnow");
        var item = (await svc.AddFolderAsync(path, CancellationToken.None))!;
        await svc.UpdateMetaAsync(item.Id, m => m with { Trusted = true }, CancellationToken.None);
        Assert.True(svc.IsTrusted(item.Id));

        // Replaced on disk, no scan or refresh since: the recorded decision says "trusted", the live folder does not.
        Directory.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-3));
        Assert.True(svc.GetMeta(item.Id).Trusted);
        Assert.False(svc.IsTrusted(item.Id));

        var profile = svc.GetProfiles(item.Id)[0];
        await using var runs = new Tezgah.Core.Execution.RunService(svc, Tezgah.Core.Safety.PathGuard.Default, new Tezgah.Core.Execution.ProcessHost());
        await Assert.ThrowsAsync<UntrustedItemException>(() => runs.StartAsync(item.Id, profile, profile.Command, CancellationToken.None));

        // Unrelated meta edits never launder the stale decision onto the new folder...
        await svc.UpdateMetaAsync(item.Id, m => m with { Favorite = true }, CancellationToken.None);
        Assert.False(svc.GetMeta(item.Id).Trusted);
        Assert.False(svc.IsTrusted(item.Id));

        // ...an explicit new confirmation (after the item was re-read) binds to the folder that is there now.
        await svc.RefreshItemAsync(item.Id, fetchGit: false, CancellationToken.None);
        await svc.UpdateMetaAsync(item.Id, m => m with { Trusted = true }, CancellationToken.None);
        Assert.True(svc.IsTrusted(item.Id));
    }

    [Fact]
    public async Task Folder_recreated_with_its_old_creation_time_is_not_trusted()
    {
        using var svc = NewService();
        var path = Project("tunnel");
        var item = (await svc.AddFolderAsync(path, CancellationToken.None))!;
        await svc.UpdateMetaAsync(item.Id, m => m with { Trusted = true }, CancellationToken.None);
        Assert.True(svc.IsTrusted(item.Id));
        var created = Directory.GetCreationTimeUtc(path);

        // Deleted and immediately re-created under the same name with different code. NTFS tunneling can hand the new
        // folder the old creation time; restoring it explicitly makes the test independent of the tunnel cache.
        Directory.Delete(path, true);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "go.mod"), "module evil");
        Directory.SetCreationTimeUtc(path, created);
        Assert.Equal(created, Directory.GetCreationTimeUtc(path));

        Assert.False(svc.IsTrusted(item.Id)); // at the point of use, before any rescan
        await svc.ScanAsync(null, CancellationToken.None);
        Assert.False(svc.GetMeta(item.Id).Trusted);
        Assert.Null(svc.GetMeta(item.Id).TrustAnchor);
    }

    [Fact]
    public void Folder_identity_is_stable_for_the_same_folder_and_differs_for_a_recreated_one()
    {
        var path = _dir.Sub("ident");
        var first = CatalogService.LiveTrustAnchor(path);
        Assert.NotNull(first);
        Assert.StartsWith("fid1:", first, StringComparison.Ordinal);
        Assert.Equal(first, CatalogService.LiveTrustAnchor(path));
        Assert.Equal(first, CatalogService.LiveTrustAnchor(path + Path.DirectorySeparatorChar));

        var created = Directory.GetCreationTimeUtc(path);
        Directory.Delete(path);
        Directory.CreateDirectory(path);
        Directory.SetCreationTimeUtc(path, created);
        Assert.NotEqual(first, CatalogService.LiveTrustAnchor(path));

        // Not a folder, or a junction (retargetable without changing its own identity): no identity at all.
        Assert.Null(CatalogService.LiveTrustAnchor(_dir.File("plain.txt", "x")));
        var link = Path.Combine(_dir.Path, "link");
        if (TestTools.TryCreateJunction(link, path))
        {
            Assert.Null(CatalogService.LiveTrustAnchor(link));
        }
    }

    [Fact]
    public async Task Trust_recorded_with_a_creation_time_anchor_is_revoked_cleanly()
    {
        var path = Project("legacy");
        var id = ItemIds.ForPath(path);
        var storePath = Path.Combine(_dir.Path, "c.json");

        // What an older Tezgah wrote: no folder identity on the item, the creation time (UTC ticks) as the anchor.
        var ticks = Directory.GetCreationTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using (var oldStore = new CatalogStore(storePath))
        {
            await oldStore.SaveAsync(new CatalogState
            {
                Items = [new CatalogItem { Id = id, Kind = ItemKind.ProjectFolder, Source = Sources.FileSystem, Name = "legacy", Path = path, InstalledAt = Directory.GetCreationTimeUtc(path) }],
                Meta = new Dictionary<string, UserMeta> { [id] = new() { Trusted = true, TrustAnchor = ticks, Favorite = true } },
            }, CancellationToken.None);
        }

        using var svc = NewService();
        await svc.InitializeAsync(CancellationToken.None);
        Assert.False(svc.IsTrusted(id)); // no crash, just not trusted

        await svc.ScanAsync(null, CancellationToken.None);
        Assert.False(svc.GetMeta(id).Trusted);
        Assert.Null(svc.GetMeta(id).TrustAnchor);
        Assert.True(svc.GetMeta(id).Favorite);

        // Confirming again binds to the new identity format.
        await svc.UpdateMetaAsync(id, m => m with { Trusted = true }, CancellationToken.None);
        Assert.True(svc.IsTrusted(id));
        Assert.StartsWith("fid1:", svc.GetMeta(id).TrustAnchor, StringComparison.Ordinal);
    }

    /// <summary>Finds the given folders like the real filesystem provider would (whatever is at the path now).</summary>
    private sealed class InspectingFsProvider(ProjectInspector inspector, params string[] paths) : IDiscoveryProvider
    {
        public string Id => Sources.FileSystem;

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public async Task<IReadOnlyList<CatalogItem>> DiscoverAsync(TezgahSettings settings, IProgress<ScanProgress>? progress, CancellationToken ct)
        {
            var found = new List<CatalogItem>();
            foreach (var p in paths)
            {
                if (await inspector.InspectAsync(p, fetchGit: false, force: false, computeSize: false, ct) is { } item)
                {
                    found.Add(item);
                }
            }

            return found;
        }
    }

    private const string CloneUrl = "https://example.invalid/a/b";

    private CatalogService NewScanningService(string path)
    {
        var git = new FetchRecordingGit();
        return new CatalogService([new InspectingFsProvider(DetectorTests.NewInspector(git), path)], new CatalogStore(Path.Combine(_dir.Path, "c.json")), git,
            DetectorTests.NewInspector(git), NullLogger<CatalogService>.Instance);
    }

    private string NodeProject(string name, string script)
    {
        var p = _dir.Sub(name);
        File.WriteAllText(Path.Combine(p, "package.json"), $$"""{ "name": "{{name}}", "scripts": { "start": "{{script}}" } }""");
        return p;
    }

    /// <summary>The state CloneAsync leaves behind: an untrusted folder marked "cloned by Tezgah" and bound to its identity.</summary>
    private static async Task<CatalogItem> MarkClonedAsync(CatalogService svc, string path)
    {
        var item = (await svc.AddFolderAsync(path, CancellationToken.None))!;
        await svc.UpdateMetaAsync(item.Id, m => m with { Trusted = false, TrustAnchor = null, ClonedFrom = CloneUrl }, CancellationToken.None);
        Assert.Equal(CatalogService.LiveTrustAnchor(path), svc.GetMeta(item.Id).CloneAnchor);
        Assert.Equal(CloneUrl, svc.VerifiedCloneSource(item.Id));
        return item;
    }

    [Fact]
    public async Task Clone_mark_is_dropped_when_an_untrusted_clone_is_replaced()
    {
        var path = NodeProject("cloned", "node a.js");
        using var svc = NewScanningService(path);
        var item = await MarkClonedAsync(svc, path);
        await svc.ScanAsync(null, CancellationToken.None);
        Assert.Equal(CloneUrl, svc.GetMeta(item.Id).ClonedFrom); // same folder: the mark survives a rescan

        // Deleted outside Tezgah and replaced by different code (no .git, no homepage) before the next scan.
        Directory.Delete(path, true);
        NodeProject("cloned", "node evil.js");
        Assert.Null(svc.VerifiedCloneSource(item.Id)); // not "cloned by Tezgah" even before any rescan

        await svc.ScanAsync(null, CancellationToken.None);

        Assert.Contains(svc.State.Items, i => i.Id == item.Id);
        Assert.Null(svc.GetMeta(item.Id).ClonedFrom);
        Assert.Null(svc.GetMeta(item.Id).CloneAnchor);
        Assert.Null(svc.VerifiedCloneSource(item.Id));
    }

    [Fact]
    public async Task Clone_mark_is_dropped_when_a_scan_sees_the_clone_missing()
    {
        var path = NodeProject("cloned2", "node a.js");
        using var svc = NewScanningService(path);
        var item = await MarkClonedAsync(svc, path);

        Directory.Delete(path, true);
        await svc.ScanAsync(null, CancellationToken.None);
        Assert.Null(svc.GetMeta(item.Id).ClonedFrom);

        NodeProject("cloned2", "node evil.js");
        await svc.ScanAsync(null, CancellationToken.None);

        Assert.Contains(svc.State.Items, i => i.Id == item.Id);
        Assert.Null(svc.GetMeta(item.Id).ClonedFrom);
        Assert.Null(svc.VerifiedCloneSource(item.Id));
    }

    [Fact]
    public async Task Refresh_of_a_replaced_or_deleted_clone_drops_the_mark()
    {
        var path = NodeProject("cloned3", "node a.js");
        using var svc = NewScanningService(path);
        var item = await MarkClonedAsync(svc, path);

        Directory.Delete(path, true);
        NodeProject("cloned3", "node evil.js");
        await svc.RefreshItemAsync(item.Id, fetchGit: false, CancellationToken.None);
        Assert.Null(svc.GetMeta(item.Id).ClonedFrom);

        item = await MarkClonedAsync(svc, path);
        Directory.Delete(path, true);
        await svc.RefreshItemAsync(item.Id, fetchGit: false, CancellationToken.None);
        Assert.Null(svc.GetMeta(item.Id).ClonedFrom);
    }

    [Fact]
    public async Task Unrelated_meta_edits_never_rebind_the_clone_mark_to_a_replaced_folder()
    {
        var path = NodeProject("cloned4", "node a.js");
        using var svc = NewScanningService(path);
        var item = await MarkClonedAsync(svc, path);
        var anchor = svc.GetMeta(item.Id).CloneAnchor;

        Directory.Delete(path, true);
        NodeProject("cloned4", "node evil.js");
        await svc.UpdateMetaAsync(item.Id, m => m with { Favorite = true, CloneAnchor = CatalogService.LiveTrustAnchor(path) }, CancellationToken.None);

        Assert.Equal(anchor, svc.GetMeta(item.Id).CloneAnchor);
        Assert.Null(svc.VerifiedCloneSource(item.Id));
    }

    [Fact]
    public void Reconcile_drops_the_clone_mark_of_untrusted_entries()
    {
        var a = new CatalogItem { Id = "path:c:\\a", Kind = ItemKind.ProjectFolder, Source = Sources.FileSystem, Name = "a", Path = @"C:\a", FolderIdentity = "fid1:1:2:3" };
        UserMeta Cloned(string? anchor) => new() { Trusted = false, ClonedFrom = CloneUrl, CloneAnchor = anchor, Favorite = true };

        // Missing path (pruned), different folder, unknown identity: dropped; same folder: kept. Other user data stays.
        var missing = CatalogService.ReconcileTrust([], new Dictionary<string, UserMeta> { [a.Id] = Cloned("fid1:1:2:3") }, pruneMissingPaths: true);
        Assert.Null(missing[a.Id].ClonedFrom);
        Assert.True(missing[a.Id].Favorite);
        Assert.Null(CatalogService.ReconcileTrust([a], new Dictionary<string, UserMeta> { [a.Id] = Cloned("fid1:9:9:9") }, false)[a.Id].ClonedFrom);
        Assert.Null(CatalogService.ReconcileTrust([a], new Dictionary<string, UserMeta> { [a.Id] = Cloned(null) }, false)[a.Id].ClonedFrom);
        Assert.Null(CatalogService.ReconcileTrust([a with { FolderIdentity = null }], new Dictionary<string, UserMeta> { [a.Id] = Cloned("fid1:1:2:3") }, false)[a.Id].ClonedFrom);
        Assert.Equal(CloneUrl, CatalogService.ReconcileTrust([a], new Dictionary<string, UserMeta> { [a.Id] = Cloned("fid1:1:2:3") }, false)[a.Id].ClonedFrom);
    }

    [Fact]
    public async Task Fetch_is_refused_for_a_replaced_folder_before_any_rescan()
    {
        var git = new FetchRecordingGit();
        using var svc = NewService(git);
        var path = _dir.Sub("fetchswap");
        _dir.Sub("fetchswap", ".git");
        var item = (await svc.AddFolderAsync(path, CancellationToken.None))!;
        await svc.UpdateMetaAsync(item.Id, m => m with { Trusted = true }, CancellationToken.None);
        Directory.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-5));
        git.Fetches.Clear();

        await svc.RefreshItemAsync(item.Id, fetchGit: true, CancellationToken.None);

        Assert.DoesNotContain(true, git.Fetches);
    }

    [Fact]
    public void Unknown_folder_identity_never_matches()
    {
        var a = new CatalogItem { Id = "path:c:\\a", Kind = ItemKind.ProjectFolder, Source = Sources.FileSystem, Name = "a", Path = @"C:\a" };
        Assert.Null(CatalogService.TrustAnchorOf(a));
        Assert.Null(CatalogService.LiveTrustAnchor(Path.Combine(_dir.Path, "does-not-exist")));

        // Legacy/forged "unknown" anchors (the old constant) are revoked instead of comparing equal.
        var meta = new Dictionary<string, UserMeta> { [a.Id] = new UserMeta { Trusted = true, TrustAnchor = "unknown" } };
        var reconciled = CatalogService.ReconcileTrust([a], meta, pruneMissingPaths: false);
        Assert.False(reconciled[a.Id].Trusted);
        Assert.False(CatalogService.IsTrustValid(a, new UserMeta { Trusted = true, TrustAnchor = "unknown" }));
    }

    [Fact]
    public async Task Refresh_of_deleted_folder_revokes_trust()
    {
        using var svc = NewService();
        var path = Project("gone");
        var item = (await svc.AddFolderAsync(path, CancellationToken.None))!;
        await svc.UpdateMetaAsync(item.Id, m => m with { Trusted = true }, CancellationToken.None);
        Directory.Delete(path, true);

        await svc.RefreshItemAsync(item.Id, fetchGit: false, CancellationToken.None);

        Assert.False(svc.GetMeta(item.Id).Trusted);
    }

    [Fact]
    public async Task Refresh_never_fetches_for_untrusted_repositories()
    {
        var git = new FetchRecordingGit();
        using var svc = NewService(git);
        var path = _dir.Sub("repo");
        _dir.Sub("repo", ".git");
        var item = (await svc.AddFolderAsync(path, CancellationToken.None))!;
        git.Fetches.Clear();

        await svc.RefreshItemAsync(item.Id, fetchGit: true, CancellationToken.None);
        await svc.UpdateMetaAsync(item.Id, m => m with { Trusted = true }, CancellationToken.None);
        await svc.RefreshItemAsync(item.Id, fetchGit: true, CancellationToken.None);

        // Untrusted: git is not run at all (metadata is read without it); trusted: the CLI runs and may fetch.
        Assert.Equal([true], git.Fetches);
    }
    private sealed class FixedFsProvider(Func<IReadOnlyList<CatalogItem>> items) : IDiscoveryProvider
    {
        public string Id => Sources.FileSystem;

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyList<CatalogItem>> DiscoverAsync(TezgahSettings settings, IProgress<ScanProgress>? progress, CancellationToken ct) =>
            Task.FromResult(items());
    }

    private sealed class CommitTimeGit(DateTimeOffset at) : IGitClient
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<GitInfo?> GetInfoAsync(string repoPath, bool fetch, CancellationToken ct) =>
            Task.FromResult<GitInfo?>(new GitInfo { Branch = "main", LastCommitAt = at, LastCommitSubject = "real" });

        public Task<CommandResult> PullAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Trusted_git_refresh_recomputes_last_activity_from_the_real_commit_time()
    {
        var path = _dir.Sub("trusted-repo");
        _dir.Sub("trusted-repo", ".git");
        var old = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var commit = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);
        CatalogItem Scanned() => new()
        {
            Id = ItemIds.ForPath(path),
            Kind = ItemKind.GitRepository,
            Source = Sources.FileSystem,
            Name = "trusted-repo",
            Path = path,
            LastActivity = old,
            InstalledAt = Directory.GetCreationTimeUtc(path),
            FolderIdentity = CatalogService.LiveTrustAnchor(path),
            Git = new GitInfo { StatusUnknown = true, UnpushedCommits = null },
        };

        var git = new CommitTimeGit(commit);
        using var svc = new CatalogService([new FixedFsProvider(() => [Scanned()])], new CatalogStore(Path.Combine(_dir.Path, "c.json")), git,
            DetectorTests.NewInspector(git), NullLogger<CatalogService>.Instance);
        await svc.ScanAsync(null, CancellationToken.None);
        var id = ItemIds.ForPath(path);
        await svc.UpdateMetaAsync(id, m => m with { Trusted = true }, CancellationToken.None);
        Assert.True(svc.IsTrusted(id));

        await svc.ScanAsync(null, CancellationToken.None);

        var item = svc.State.Items.Single(i => i.Id == id);
        Assert.Equal(commit, item.Git!.LastCommitAt);
        Assert.Equal(commit, item.LastActivity);
    }
}
