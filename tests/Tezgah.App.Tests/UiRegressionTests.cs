using System.Globalization;
using Tezgah.App.Demo;
using Tezgah.App.ViewModels;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.App.Tests;

public class UiRegressionTests
{
    private static ItemViewModel Item(string name, string? note = null) => new(
        new CatalogItem { Id = "x:" + name, Kind = ItemKind.InstalledApp, Source = Sources.Registry, Name = name },
        new UserMeta { Note = note }, [], false, DateTimeOffset.Now, new KeyStrings());

    [Theory]
    [InlineData("IntelliJ IDEA", "intellij")]
    [InlineData("IntelliJ IDEA", "INTELLIJ")]
    [InlineData("IntelliJ IDEA", "İntellij")]
    [InlineData("Inkscape", "İNKSCAPE")]
    [InlineData("ILSpy", "ılspy")]
    [InlineData("IIS Express", "iis")]
    [InlineData("İstanbul Projesi", "istanbul")]
    [InlineData("Şirket Ürün", "şirket ürün")]
    [InlineData("Ürün", "urun")]
    public void Search_is_independent_of_the_turkish_i(string name, string term)
    {
        var previous = Formatting.Culture;
        Formatting.Culture = new CultureInfo("tr-TR");
        try
        {
            Assert.True(Item(name).Matches(term));
        }
        finally
        {
            Formatting.Culture = previous;
        }
    }

    [Fact]
    public void Ambiguous_artifacts_are_not_preselected_for_cleanup()
    {
        Assert.False(new ArtifactViewModel(new ArtifactDir(@"C:\p\build", "node", 1, Ambiguous: true), @"C:\p").IsSelected);
        Assert.True(new ArtifactViewModel(new ArtifactDir(@"C:\p\node_modules", "node", 1), @"C:\p").IsSelected);
    }

    [Fact]
    public void Search_haystack_is_refreshed_on_update()
    {
        var vm = Item("Tool", note: "alpha");
        Assert.True(vm.Matches("alpha"));
        vm.Update(vm.Item, new UserMeta { Note = "beta" }, [], false, DateTimeOffset.Now);
        Assert.False(vm.Matches("alpha"));
        Assert.True(vm.Matches("beta"));
    }

    private sealed class CloningCatalog : ICatalogService
    {
        public CatalogState State { get; } = new();

        public bool IsScanning => false;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public Task ScanAsync(IProgress<ScanProgress>? progress, CancellationToken ct) => Task.CompletedTask;

        public Task RefreshItemAsync(string itemId, bool fetchGit, CancellationToken ct) => Task.CompletedTask;

        public Task UpdateMetaAsync(string itemId, Func<UserMeta, UserMeta> update, CancellationToken ct) => Task.CompletedTask;

        public Task UpdateSettingsAsync(Func<TezgahSettings, TezgahSettings> update, CancellationToken ct) => Task.CompletedTask;

        public Task RemoveItemAsync(string itemId, CancellationToken ct) => Task.CompletedTask;

        public Task<CatalogItem?> AddFolderAsync(string path, CancellationToken ct) => Task.FromResult<CatalogItem?>(null);

        public async Task<CatalogItem?> CloneAsync(string gitUrl, string targetParent, IProgress<string>? progress, CancellationToken ct)
        {
            await Task.Yield();
            return new CatalogItem { Id = "path:c:\\x\\y", Kind = ItemKind.GitRepository, Source = Sources.FileSystem, Name = "y", Path = @"C:\x\y" };
        }

        public UserMeta GetMeta(string itemId) => new();

        public IReadOnlyList<RunProfile> GetProfiles(string itemId) => [];

        public bool IsForgotten(CatalogItem item) => false;
    }

    [Fact]
    public async Task Clone_dialog_asks_to_close_only_after_it_stopped_being_busy()
    {
        var vm = new CloneViewModel(new CloningCatalog(), new FakeDialogs(), new KeyStrings(), @"C:\x") { Url = "https://github.com/x/y" };
        var closes = 0;
        bool? busyWhenClosing = null;
        vm.CloseRequested += (_, _) =>
        {
            closes++;
            busyWhenClosing = vm.IsBusy;
        };

        await vm.CloneCommand.ExecuteAsync(null);

        Assert.Equal(1, closes);
        Assert.False(busyWhenClosing);
        Assert.NotNull(vm.Result);
    }

    [Fact]
    public async Task Runs_of_an_item_that_left_the_catalog_stay_reachable_and_block_silent_exit()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        var item = h.Item("vite-react-dashboard");
        var session = await h.Vm.StartAsync(item, item.DefaultProfile!.Profile);
        Assert.NotNull(session);

        await h.Catalog.RemoveItemAsync(item.Id, CancellationToken.None);
        h.Vm.SyncFromCatalog();

        Assert.Contains(h.Vm.AllItems, i => i.Id == item.Id);
        Assert.True(h.Vm.HasLiveRuns);
        h.Dialogs.ConfirmAnswer = false;
        Assert.False(await h.Vm.ConfirmCloseAsync());

        await h.Runs.StopAllAsync(CancellationToken.None);
        for (var i = 0; i < 40 && h.Vm.AllItems.Any(x => x.Id == item.Id); i++)
        {
            await Task.Delay(50);
            h.Ui.Tick();
        }

        Assert.DoesNotContain(h.Vm.AllItems, i => i.Id == item.Id);
    }

    [Fact]
    public async Task Filtering_keeps_the_selection_and_an_open_note_edit()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        var item = h.Item("whisper-transcriber");
        h.Vm.SelectedItem = item;
        h.Vm.BeginEditMetaCommand.Execute(null);
        Assert.True(h.Vm.IsEditingMeta);

        h.Vm.SearchText = "zzz-no-such-item";

        Assert.Same(item, h.Vm.SelectedItem);
        Assert.Contains(item, h.Vm.VisibleItems);
        Assert.True(h.Vm.IsEditingMeta);

        h.Vm.SelectedItem = null; // what a ListBox pushes when its selected item disappears
        Assert.True(h.Vm.IsEditingMeta);

        h.Vm.SelectedItem = h.Item("vite-react-dashboard"); // a real change of selection ends the edit
        Assert.False(h.Vm.IsEditingMeta);
    }
}
