using System.Collections.ObjectModel;
using Tezgah.App.ViewModels;
using Tezgah.Core.Model;

namespace Tezgah.App.Tests;

public class MainViewModelTests
{
    [Fact]
    public async Task Initialize_loads_demo_catalog_and_counts()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();

        Assert.True(h.Vm.IsLoaded);
        Assert.False(h.Vm.IsEmpty);
        Assert.Equal(h.Catalog.State.Items.Count, h.Vm.VisibleItems.Count);
        Assert.Equal(h.Vm.CountRepos + h.Vm.CountApps + h.Vm.CountTools, h.Vm.CountAll);
        Assert.True(h.Vm.CountForgotten > 0);
        Assert.StartsWith("Status_Totals(", h.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_catalog_shows_empty_state()
    {
        var h = new Harness(empty: true);
        await h.Vm.InitializeAsync();
        Assert.True(h.Vm.IsEmpty);
    }

    [Theory]
    [InlineData(ItemFilter.Apps, ItemKind.InstalledApp)]
    [InlineData(ItemFilter.Tools, ItemKind.PackageTool)]
    public async Task Filter_chips_restrict_kinds(ItemFilter filter, ItemKind kind)
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        h.Vm.ActiveFilter = filter;
        Assert.NotEmpty(h.Vm.VisibleItems);
        Assert.All(h.Vm.VisibleItems, i => Assert.Equal(kind, i.Kind));
    }

    [Fact]
    public async Task Forgotten_filter_contains_the_unpushed_whisper_repo()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        h.Vm.ActiveFilter = ItemFilter.Forgotten;
        var whisper = Assert.Single(h.Vm.VisibleItems, i => i.Name == "whisper-transcriber");
        Assert.True(whisper.HasUnsavedWork);
        Assert.True(whisper.IsUnpushed);
    }

    [Fact]
    public async Task Search_matches_all_terms_across_fields()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();

        h.Vm.SearchText = "harita";                 // a tag
        Assert.Equal("parsel-sorgu", Assert.Single(h.Vm.VisibleItems).Name);

        h.Vm.SearchText = "AUTOMATIC1111";          // part of the remote URL
        Assert.Equal("stable-diffusion-webui", Assert.Single(h.Vm.VisibleItems).Name);

        h.Vm.SearchText = "python whisper";         // two terms, both required
        Assert.Equal("whisper-transcriber", Assert.Single(h.Vm.VisibleItems).Name);

        h.Vm.SearchText = "no-such-thing-xyz";
        Assert.Empty(h.Vm.VisibleItems);
        Assert.True(h.Vm.HasNoMatches);
    }

    [Fact]
    public async Task Sort_by_size_puts_largest_first()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        h.Vm.SortMode = SortMode.Size;
        var sizes = h.Vm.VisibleItems.Select(i => i.Item.SizeBytes ?? -1).ToList();
        Assert.Equal(sizes.OrderByDescending(s => s), sizes);
        Assert.Equal("stable-diffusion-webui", h.Vm.VisibleItems[0].Name);
    }

    [Fact]
    public async Task Untrusted_run_asks_and_does_nothing_when_declined()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        var item = h.Item("whisper-transcriber");
        h.Dialogs.TrustAnswer = false;

        var session = await h.Vm.StartAsync(item, item.DefaultProfile!.Profile);

        Assert.Null(session);
        var prompt = Assert.Single(h.Dialogs.TrustPrompts);
        Assert.Equal(item.DefaultProfile!.CommandLine, prompt.CommandLine);
        Assert.Equal(item.Path, prompt.Path);
        Assert.False(h.Catalog.GetMeta(item.Id).Trusted);
        Assert.Empty(h.Runs.Running);
    }

    [Fact]
    public async Task Untrusted_run_marks_trusted_then_starts_when_confirmed()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        var item = h.Item("whisper-transcriber");
        h.Dialogs.TrustAnswer = true;

        var session = await h.Vm.StartAsync(item, item.DefaultProfile!.Profile);

        Assert.NotNull(session);
        Assert.True(h.Catalog.GetMeta(item.Id).Trusted);
        Assert.True(h.Item("whisper-transcriber").IsRunning);
        Assert.Same(session, item.SelectedRun);

        await h.Runs.StopAllAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Trusted_run_does_not_prompt_and_output_flows_into_the_console()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        var item = h.Item("vite-react-dashboard");

        var session = await h.Vm.StartAsync(item, item.DefaultProfile!.Profile);
        Assert.NotNull(session);
        Assert.Empty(h.Dialogs.TrustPrompts);

        // wait for the fake dev server to print its URL, flushing like the UI timer does
        for (var i = 0; i < 60 && session!.Urls.Count == 0; i++)
        {
            await Task.Delay(50);
            h.Ui.Tick();
        }

        Assert.Contains(session!.Urls, u => u.Port == 5173);
        Assert.Contains(session.Lines, l => l.Text.Contains("VITE", StringComparison.Ordinal));
        Assert.Equal(1, h.Vm.RunningCount);

        h.Vm.SelectedItem = item;
        await h.Vm.StopCommand.ExecuteAsync(null);
        h.Ui.Tick();
        Assert.False(session.IsRunning);
        Assert.Equal(0, h.Vm.RunningCount);
    }

    [Fact]
    public async Task Confirm_close_stops_running_processes()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        var item = h.Item("vite-react-dashboard");
        await h.Vm.StartAsync(item, item.DefaultProfile!.Profile);

        h.Dialogs.ConfirmAnswer = false;
        Assert.False(await h.Vm.ConfirmCloseAsync());
        Assert.NotEmpty(h.Runs.Running);

        h.Dialogs.ConfirmAnswer = true;
        Assert.True(await h.Vm.ConfirmCloseAsync());
        Assert.Empty(h.Runs.Running);
    }

    [Fact]
    public async Task Uninstall_success_removes_item_from_list()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        h.Vm.SelectedItem = h.Item("yt-dlp");
        h.Dialogs.OnUninstall = vm =>
        {
            vm.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.False(vm.RequiresTypedConfirmation);
            vm.ExecuteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        };

        await h.Vm.UninstallCommand.ExecuteAsync(null);

        Assert.DoesNotContain(h.Vm.VisibleItems, i => i.Name == "yt-dlp");
        Assert.Null(h.Vm.SelectedItem);
    }

    [Fact]
    public async Task Favorite_toggle_updates_filter()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        var seven = h.Item("7-Zip 24.09 (x64)");
        Assert.False(seven.IsFavorite);

        await h.Vm.ToggleFavoriteCommand.ExecuteAsync(seven);
        h.Vm.ActiveFilter = ItemFilter.Favorites;

        Assert.Contains(h.Vm.VisibleItems, i => i.Name == "7-Zip 24.09 (x64)");
    }

    [Fact]
    public void Reconcile_keeps_instances_and_matches_order()
    {
        var a = new object();
        var b = new object();
        var c = new object();
        var d = new object();
        var target = new ObservableCollection<object> { a, b, c };
        var events = 0;
        target.CollectionChanged += (_, _) => events++;

        MainViewModel.Reconcile(target, [c, a, d]);

        Assert.Equal([c, a, d], target);
        Assert.True(events <= 4);
    }
}
