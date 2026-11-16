using Tezgah.App.Demo;
using Tezgah.App.Services;
using Tezgah.App.ViewModels;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.App.Tests;

/// <summary>git pull / fetch honour repository-controlled config and hooks, so they go through the workspace-trust gate.</summary>
public class GitTrustGateTests
{
    private sealed class RecordingGit(IGitClient inner) : IGitClient
    {
        public int Pulls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => inner.IsAvailableAsync(ct);

        public Task<GitInfo?> GetInfoAsync(string repoPath, bool fetch, CancellationToken ct) => inner.GetInfoAsync(repoPath, fetch, ct);

        public Task<CommandResult> PullAsync(string repoPath, CancellationToken ct)
        {
            Pulls++;
            return Task.FromResult(new CommandResult(0, "Already up to date.", string.Empty, false));
        }
    }

    private sealed class RecordingCatalog(DemoCatalogService inner) : ICatalogService
    {
        public List<bool> RefreshFetches { get; } = [];

        public CatalogState State => inner.State;

        public bool IsScanning => inner.IsScanning;

        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);

        public Task ScanAsync(IProgress<ScanProgress>? progress, CancellationToken ct) => inner.ScanAsync(progress, ct);

        public Task RefreshItemAsync(string itemId, bool fetchGit, CancellationToken ct)
        {
            RefreshFetches.Add(fetchGit);
            return Task.CompletedTask;
        }

        public Task UpdateMetaAsync(string itemId, Func<UserMeta, UserMeta> update, CancellationToken ct) => inner.UpdateMetaAsync(itemId, update, ct);

        public Task UpdateSettingsAsync(Func<TezgahSettings, TezgahSettings> update, CancellationToken ct) => inner.UpdateSettingsAsync(update, ct);

        public Task RemoveItemAsync(string itemId, CancellationToken ct) => inner.RemoveItemAsync(itemId, ct);

        public Task<CatalogItem?> AddFolderAsync(string path, CancellationToken ct) => inner.AddFolderAsync(path, ct);

        public Task<CatalogItem?> CloneAsync(string gitUrl, string targetParent, IProgress<string>? progress, CancellationToken ct) =>
            inner.CloneAsync(gitUrl, targetParent, progress, ct);

        public UserMeta GetMeta(string itemId) => inner.GetMeta(itemId);

        public IReadOnlyList<RunProfile> GetProfiles(string itemId) => inner.GetProfiles(itemId);

        public bool IsForgotten(CatalogItem item) => inner.IsForgotten(item);
    }

    private static (MainViewModel Vm, FakeDialogs Dialogs, RecordingGit Git, RecordingCatalog Catalog) Build()
    {
        var demo = new DemoCatalogService(new DemoOptions(false));
        var catalog = new RecordingCatalog(demo);
        var runs = new DemoRunService(demo);
        var git = new RecordingGit(new DemoGitClient(demo));
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(catalog, runs, new DemoUninstallService(demo, runs), new DemoCleanupService(demo), git,
            dialogs, new FakeShell(), new FakeClipboard(), new KeyStrings(), new InlineDispatcher(), new FakeAppearance(), new AppMode(true));
        return (vm, dialogs, git, catalog);
    }

    [Fact]
    public async Task Pull_on_untrusted_repo_asks_and_does_not_pull_when_declined()
    {
        var (vm, dialogs, git, _) = Build();
        await vm.InitializeAsync();
        vm.SelectedItem = vm.AllItems.Single(i => i.Name == "whisper-transcriber");
        dialogs.TrustAnswer = false;

        await vm.PullCommand.ExecuteAsync(null);

        var prompt = Assert.Single(dialogs.TrustPrompts);
        Assert.Equal("git pull --ff-only", prompt.CommandLine);
        Assert.False(prompt.RemoteVerified);
        Assert.Equal(0, git.Pulls);
    }

    [Fact]
    public async Task Pull_on_trusted_repo_does_not_prompt()
    {
        var (vm, dialogs, git, _) = Build();
        await vm.InitializeAsync();
        vm.SelectedItem = vm.AllItems.First(i => i.IsTrusted && i.HasGit);

        await vm.PullCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.TrustPrompts);
        Assert.Equal(1, git.Pulls);
    }

    [Fact]
    public async Task Refresh_of_untrusted_repo_never_fetches()
    {
        var (vm, _, _, catalog) = Build();
        await vm.InitializeAsync();
        vm.SelectedItem = vm.AllItems.Single(i => i.Name == "whisper-transcriber");
        await vm.RefreshItemCommand.ExecuteAsync(null);

        vm.SelectedItem = vm.AllItems.First(i => i.IsTrusted && i.HasGit);
        await vm.RefreshItemCommand.ExecuteAsync(null);

        Assert.Equal([false, true], catalog.RefreshFetches);
    }

    [Fact]
    public async Task Trust_prompt_marks_remote_verified_only_for_tezgah_clones()
    {
        var (vm, dialogs, _, catalog) = Build();
        await vm.InitializeAsync();
        var item = vm.AllItems.Single(i => i.Name == "whisper-transcriber");
        await catalog.UpdateMetaAsync(item.Id, m => m with { ClonedFrom = item.Git!.RemoteUrl }, CancellationToken.None);
        vm.SelectedItem = item;
        dialogs.TrustAnswer = false;

        await vm.PullCommand.ExecuteAsync(null);

        Assert.True(Assert.Single(dialogs.TrustPrompts).RemoteVerified);
    }
}
