using Tezgah.App.Demo;
using Tezgah.App.Services;
using Tezgah.App.ViewModels;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.App.Tests;

/// <summary>
/// The trust prompt's approval covers exactly the command it showed: never a stale view-model copy, and never whatever the
/// catalog holds for the same (positional) profile id after a re-read or a scan.
/// </summary>
public class TrustPromptBindingTests
{
    /// <summary>
    /// Demo catalog whose recorded trust can be "stale" (the trusted folder was replaced on disk) and whose re-read of an
    /// item can be scripted, like <c>CatalogService.RefreshItemAsync</c> after a folder swap.
    /// </summary>
    internal sealed class SwappingCatalog(DemoCatalogService inner) : ICatalogService
    {
        public DemoCatalogService Inner => inner;

        public HashSet<string> Stale { get; } = [];

        public Func<string, Task>? OnRefresh { get; set; }

        public CatalogState State => inner.State;

        public bool IsScanning => inner.IsScanning;

        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);

        public Task ScanAsync(IProgress<ScanProgress>? progress, CancellationToken ct) => inner.ScanAsync(progress, ct);

        public Task RefreshItemAsync(string itemId, bool fetchGit, CancellationToken ct) => OnRefresh?.Invoke(itemId) ?? Task.CompletedTask;

        public Task UpdateMetaAsync(string itemId, Func<UserMeta, UserMeta> update, CancellationToken ct) => inner.UpdateMetaAsync(itemId, update, ct);

        public Task UpdateSettingsAsync(Func<TezgahSettings, TezgahSettings> update, CancellationToken ct) => inner.UpdateSettingsAsync(update, ct);

        public Task RemoveItemAsync(string itemId, CancellationToken ct) => inner.RemoveItemAsync(itemId, ct);

        public Task<CatalogItem?> AddFolderAsync(string path, CancellationToken ct) => inner.AddFolderAsync(path, ct);

        public Task<CatalogItem?> CloneAsync(string gitUrl, string targetParent, IProgress<string>? progress, CancellationToken ct) =>
            inner.CloneAsync(gitUrl, targetParent, progress, ct);

        public UserMeta GetMeta(string itemId) => inner.GetMeta(itemId);

        public bool IsTrusted(string itemId) => inner.GetMeta(itemId).Trusted && !Stale.Contains(itemId);

        public IReadOnlyList<RunProfile> GetProfiles(string itemId) => inner.GetProfiles(itemId);

        public bool IsForgotten(CatalogItem item) => inner.IsForgotten(item);

        /// <summary>The folder now holds different code: same profile ids, the given profile id runs something else.</summary>
        public CommandSpec SwapCommand(string itemId, string profileId)
        {
            var item = inner.State.Items.Single(i => i.Id == itemId);
            var replaced = new CommandSpec { FileName = "cmd.exe", Arguments = ["/d", "/c", "echo REPLACED"], WorkingDirectory = item.Path! };
            inner.ReplaceItem(item with { RunProfiles = [.. item.RunProfiles.Select(p => p.Id == profileId ? p with { Command = replaced } : p)] });
            return replaced;
        }

        public void DropProfile(string itemId, string profileId)
        {
            var item = inner.State.Items.Single(i => i.Id == itemId);
            inner.ReplaceItem(item with { RunProfiles = [.. item.RunProfiles.Where(p => p.Id != profileId)] });
        }
    }

    private static (MainViewModel Vm, FakeDialogs Dialogs, SwappingCatalog Catalog, DemoRunService Runs) Build()
    {
        var catalog = new SwappingCatalog(new DemoCatalogService(new DemoOptions(false)));
        var runs = new DemoRunService(catalog);
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(catalog, runs, new DemoUninstallService(catalog, runs), new DemoCleanupService(catalog), new DemoGitClient(catalog),
            dialogs, new FakeShell(), new FakeClipboard(), new KeyStrings(), new InlineDispatcher(), new FakeAppearance(), new AppMode(true));
        return (vm, dialogs, catalog, runs);
    }

    [Fact]
    public async Task Stale_trust_prompt_shows_and_runs_the_command_of_the_reread_folder()
    {
        var (vm, dialogs, catalog, runs) = Build();
        await vm.InitializeAsync();
        var item = vm.AllItems.Single(i => i.Name == "whisper-transcriber");
        var old = item.DefaultProfile!.Profile;

        // Trust was recorded for the folder, which has since been deleted and re-created with different code.
        await catalog.UpdateMetaAsync(item.Id, m => m with { Trusted = true }, CancellationToken.None);
        catalog.Stale.Add(item.Id);
        CommandSpec? replaced = null;
        catalog.OnRefresh = async id =>
        {
            replaced = catalog.SwapCommand(id, old.Id);
            catalog.Stale.Remove(id);
            await catalog.UpdateMetaAsync(id, m => m with { Trusted = false }, CancellationToken.None); // revoked by the re-read
        };
        dialogs.TrustAnswer = true;

        var session = await vm.StartAsync(item, old);

        var prompt = Assert.Single(dialogs.TrustPrompts);
        Assert.NotNull(replaced);
        Assert.Equal(replaced.Display, prompt.CommandLine);
        Assert.NotEqual(old.Command.Display, prompt.CommandLine);
        Assert.NotNull(session);
        Assert.True(CommandSpec.IsSameInvocation(replaced, session.Process.Profile.Command)); // what ran is what was shown
        await runs.StopAllAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Profile_missing_after_the_reread_aborts_without_prompting()
    {
        var (vm, dialogs, catalog, runs) = Build();
        await vm.InitializeAsync();
        var item = vm.AllItems.Single(i => i.Name == "whisper-transcriber");
        var old = item.DefaultProfile!.Profile;
        await catalog.UpdateMetaAsync(item.Id, m => m with { Trusted = true }, CancellationToken.None);
        catalog.Stale.Add(item.Id);
        catalog.OnRefresh = async id =>
        {
            catalog.DropProfile(id, old.Id);
            catalog.Stale.Remove(id);
            await catalog.UpdateMetaAsync(id, m => m with { Trusted = false }, CancellationToken.None);
        };
        dialogs.TrustAnswer = true;

        var session = await vm.StartAsync(item, old);

        Assert.Null(session);
        Assert.Empty(dialogs.TrustPrompts);
        Assert.Empty(runs.Running);
        Assert.False(catalog.GetMeta(item.Id).Trusted);
        Assert.StartsWith("Run_ProfileGone(", vm.Notification, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_changed_while_the_trust_prompt_was_open_is_not_run()
    {
        var (vm, dialogs, catalog, runs) = Build();
        await vm.InitializeAsync();
        var item = vm.AllItems.Single(i => i.Name == "whisper-transcriber");
        var profile = item.DefaultProfile!.Profile;
        dialogs.TrustAnswer = true;

        // A scan/refresh completes while the modal prompt is open and gives the same profile id a different command.
        dialogs.WhileTrustPromptOpen = _ => catalog.SwapCommand(item.Id, profile.Id);

        var session = await vm.StartAsync(item, profile);

        Assert.Equal(profile.Command.Display, Assert.Single(dialogs.TrustPrompts).CommandLine);
        Assert.Null(session);
        Assert.Empty(runs.Running);
        Assert.StartsWith("Run_CommandChanged(", vm.Notification, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trusted_run_is_refused_when_the_listed_command_is_stale()
    {
        var (vm, _, catalog, runs) = Build();
        await vm.InitializeAsync();
        var item = vm.AllItems.Single(i => i.Name == "vite-react-dashboard");
        var listed = item.DefaultProfile!.Profile;
        catalog.SwapCommand(item.Id, listed.Id); // e.g. a rescan landed between rendering the list and the click

        var session = await vm.StartAsync(item, listed);

        Assert.Null(session);
        Assert.Empty(runs.Running);
        Assert.StartsWith("Run_CommandChanged(", vm.Notification, StringComparison.Ordinal);
    }
}
