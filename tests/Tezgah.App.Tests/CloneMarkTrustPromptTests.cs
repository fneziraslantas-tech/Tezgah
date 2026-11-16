using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tezgah.App.Demo;
using Tezgah.App.Services;
using Tezgah.App.ViewModels;
using Tezgah.Core;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.App.Tests;

/// <summary>
/// The trust prompt calls a remote "cloned by Tezgah" only for the folder Tezgah cloned: never for different code that
/// later appeared at the same path. Uses the real catalog service over a temp folder and an in-memory store.
/// </summary>
public sealed class CloneMarkTrustPromptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tezgah-app-tests", Guid.NewGuid().ToString("N")[..12]);

    public CloneMarkTrustPromptTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class MemoryStore : ICatalogStore
    {
        private CatalogState _state = new();

        public Task<CatalogState> LoadAsync(CancellationToken ct) => Task.FromResult(_state);

        public Task SaveAsync(CatalogState state, CancellationToken ct)
        {
            _state = state;
            return Task.CompletedTask;
        }
    }

    private string NodeProject(string name, string script)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p, "package.json"), $$"""{ "name": "{{name}}", "scripts": { "start": "{{script}}" } }""");
        return p;
    }

    [Fact]
    public async Task Replaced_untrusted_clone_is_not_shown_as_cloned_by_tezgah()
    {
        var services = new ServiceCollection();
        services.AddTezgahCore();
        services.Replace(ServiceDescriptor.Singleton<ICatalogStore>(new MemoryStore()));
        await using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<ICatalogService>();
        var runs = new DemoRunService(catalog); // nothing is started in this test: every prompt is declined
        var dialogs = new FakeDialogs { TrustAnswer = false };
        using var vm = new MainViewModel(catalog, runs, new DemoUninstallService(catalog, runs), new DemoCleanupService(catalog), new DemoGitClient(catalog),
            dialogs, new FakeShell(), new FakeClipboard(), new KeyStrings(), new InlineDispatcher(), new FakeAppearance(), new AppMode(false));
        await vm.InitializeAsync();

        const string url = "https://example.invalid/a/b";
        var path = NodeProject("cloned", "node a.js");
        var added = (await catalog.AddFolderAsync(path, CancellationToken.None))!;
        await catalog.UpdateMetaAsync(added.Id, m => m with { Trusted = false, TrustAnchor = null, ClonedFrom = url }, CancellationToken.None); // as CloneAsync does
        var item = vm.AllItems.Single(i => i.Id == added.Id);

        await vm.StartAsync(item, item.Profiles[0].Profile);
        var first = Assert.Single(dialogs.TrustPrompts);
        Assert.True(first.RemoteVerified); // the folder Tezgah cloned
        Assert.Equal(url, first.Remote);

        // Deleted outside Tezgah and replaced by different code without .git or homepage; no rescan in between.
        Directory.Delete(path, recursive: true);
        NodeProject("cloned", "node evil.js");
        Assert.Equal(url, catalog.GetMeta(added.Id).ClonedFrom); // the stale record is still there...

        await vm.StartAsync(item, item.Profiles[0].Profile);

        var second = dialogs.TrustPrompts[1];
        Assert.False(second.RemoteVerified); // ...but it is not presented as "cloned by Tezgah"
        Assert.Null(second.Remote);
        Assert.Empty(runs.Running);
    }
}
