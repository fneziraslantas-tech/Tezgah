using Tezgah.App.ViewModels;
using Tezgah.Core.Model;

namespace Tezgah.App.Tests;

public class DialogViewModelTests
{
    [Fact]
    public async Task Repo_with_unpushed_work_requires_typed_name()
    {
        var h = new Harness();
        await h.Catalog.InitializeAsync(CancellationToken.None);
        var item = h.Catalog.State.Items.Single(i => i.Name == "whisper-transcriber");
        var vm = new UninstallViewModel(item.Id, item.Name, h.Uninstall, new KeyStrings());

        await vm.LoadAsync(CancellationToken.None);

        Assert.True(vm.RequiresTypedConfirmation);
        Assert.False(vm.ExecuteCommand.CanExecute(null));
        vm.TypedText = "whisper";
        Assert.False(vm.ExecuteCommand.CanExecute(null));
        vm.TypedText = "whisper-transcriber";
        Assert.True(vm.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Blocked_plan_cannot_execute()
    {
        var h = new Harness();
        await h.Catalog.InitializeAsync(CancellationToken.None);
        var item = h.Catalog.State.Items.Single(i => i.Name.Contains("WebView2", StringComparison.Ordinal));
        var vm = new UninstallViewModel(item.Id, item.Name, h.Uninstall, new KeyStrings());

        await vm.LoadAsync(CancellationToken.None);

        Assert.True(vm.IsBlocked);
        Assert.NotNull(vm.BlockReason);
        Assert.False(vm.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Optional_steps_toggle_and_mandatory_steps_stay_selected()
    {
        var h = new Harness();
        await h.Catalog.InitializeAsync(CancellationToken.None);
        var item = h.Catalog.State.Items.Single(i => i.Name == "QGIS 3.40.4 'Bratislava'");
        var vm = new UninstallViewModel(item.Id, item.Name, h.Uninstall, new KeyStrings());
        await vm.LoadAsync(CancellationToken.None);

        var optional = vm.Steps.First(s => !s.IsLocked);
        var mandatory = vm.Steps.First(s => s.IsLocked);
        var totalBefore = vm.TotalText;

        optional.IsSelected = false;
        mandatory.IsSelected = false;

        Assert.False(optional.IsSelected);
        Assert.True(mandatory.IsSelected);
        Assert.NotNull(totalBefore);
    }

    [Fact]
    public async Task Cleanup_frees_selected_artifacts_only()
    {
        var h = new Harness();
        await h.Catalog.InitializeAsync(CancellationToken.None);
        var item = h.Catalog.State.Items.Single(i => i.Name == "vite-react-dashboard");
        var vm = new CleanupViewModel(item, new Tezgah.App.Demo.DemoCleanupService(h.Catalog), new KeyStrings());
        Assert.Equal(2, vm.Artifacts.Count);
        vm.Artifacts[1].IsSelected = false;

        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Equal(vm.Artifacts[0].Artifact.SizeBytes, vm.FreedBytes);
        var updated = h.Catalog.State.Items.Single(i => i.Id == item.Id);
        Assert.Single(updated.Artifacts);
        Assert.False(vm.CleanCommand.CanExecute(null));
    }

    [Fact]
    public void Settings_round_trip_and_rescan_detection()
    {
        var original = new TezgahSettings { ScanRoots = [@"C:\src"], MaxDepth = 6 };
        var vm = new SettingsViewModel(original, new FakeDialogs(), new KeyStrings())
        {
            MaxDepth = 99,
            ExcludedText = "node_modules\r\n.venv\r\nnode_modules",
            Language = "en",
            Theme = "dark",
        };

        var updated = vm.ToSettings(original);

        Assert.Equal(32, updated.MaxDepth); // clamped
        Assert.Equal(["node_modules", ".venv"], updated.ExcludedDirectoryNames);
        Assert.Equal("en", updated.Language);
        Assert.Equal("dark", updated.Theme);
        Assert.True(vm.ScanSettingsChanged(updated));
        Assert.False(vm.ScanSettingsChanged(original));
    }

    [Theory]
    [InlineData("https://github.com/user/repo", true)]
    [InlineData("https://github.com/user/repo.git", true)]
    [InlineData("git@github.com:user/repo.git", true)]
    [InlineData("ssh://git@github.com/user/repo.git", true)]
    [InlineData("--upload-pack=evil", false)]
    [InlineData("-c core.sshCommand=calc", false)]
    [InlineData("https://github.com/user/repo extra", false)]
    [InlineData("file:///C:/secret", false)]
    [InlineData("ext::sh -c calc", false)]
    [InlineData("https://github.com/", false)]
    [InlineData("", false)]
    public void Clone_url_validation(string url, bool valid) =>
        Assert.Equal(valid, CloneViewModel.IsValidGitUrl(url));

    [Theory]
    [InlineData("https://github.com/user/my-repo.git", "my-repo")]
    [InlineData("git@github.com:user/tool", "tool")]
    public void Clone_repo_name(string url, string name) =>
        Assert.Equal(name, CloneViewModel.RepoName(url));
}
