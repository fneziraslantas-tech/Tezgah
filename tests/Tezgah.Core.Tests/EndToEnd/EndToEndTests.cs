using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;
using Tezgah.Core.Uninstall;

namespace Tezgah.Core.Tests.EndToEnd;

/// <summary>
/// End-to-end tests over the real composed core (discovery, git, catalog persistence, process host + Job Object,
/// path guard, Recycle Bin removal, audit log) against fake projects in a temp folder. Methods of one class run
/// sequentially in xunit, so only one dev server is alive at a time. Tests return early when git is not installed.
/// </summary>
public sealed class EndToEndTests
{
    private static readonly TimeSpan UrlTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Scan_finds_git_repo_and_node_project_but_not_plain_folder()
    {
        if (!EndToEndWorld.HasGit)
        {
            return;
        }

        await using var world = new EndToEndWorld();
        world.CreateProjects();
        var sp = world.BuildServices();
        var catalog = sp.GetRequiredService<ICatalogService>();

        await world.ConfigureAndScanAsync(catalog);

        var items = catalog.State.Items;
        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, i => i.Id == ItemIds.ForPath(world.PlainPath));
        Assert.All(items, i => Assert.Equal(Sources.FileSystem, i.Source));

        var repo = EndToEndWorld.Item(catalog, world.GitRepoPath);
        Assert.Equal(ItemKind.GitRepository, repo.Kind);
        Assert.Equal("demo-server", repo.Name);
        Assert.NotNull(repo.Git);
        Assert.Equal("main", repo.Git!.Branch);
        Assert.Equal("initial commit", repo.Git.LastCommitSubject);
        Assert.Equal(0, repo.Git.ModifiedFiles + repo.Git.UntrackedFiles);
        var runDefault = Assert.Single(catalog.GetProfiles(repo.Id), p => p.IsDefault);
        Assert.Equal(RunProfileKind.Run, runDefault.Kind);
        if (world.UsesPython)
        {
            Assert.Contains("python", repo.ProjectTypes);
            Assert.Equal("py:server", runDefault.Id);
        }
        else
        {
            Assert.Contains("scripts", repo.ProjectTypes);
            Assert.Equal("script:run.cmd", runDefault.Id);
        }

        var node = EndToEndWorld.Item(catalog, world.NodePath);
        Assert.Equal(ItemKind.ProjectFolder, node.Kind);
        Assert.Contains("node", node.ProjectTypes);
        Assert.Equal("E2E node project", node.Description);
        Assert.Contains(node.RunProfiles, p => p.Id == "npm:dev" && p.Kind == RunProfileKind.Run);
        Assert.Contains(node.RunProfiles, p => p.Id == "npm:install" && p.Kind == RunProfileKind.Setup);
        var nodeModules = Assert.Single(node.Artifacts, a => Path.GetFileName(a.Path) == "node_modules");
        Assert.True(nodeModules.SizeBytes >= 64 * 1024);

        // Settings and scan result were persisted to the private catalog file only.
        Assert.True(File.Exists(world.CatalogPath));
        Assert.NotNull(catalog.State.LastScanAt);
    }

    [Fact]
    public async Task Run_requires_trust_then_detects_url_and_stop_kills_the_process_tree()
    {
        if (!EndToEndWorld.HasGit)
        {
            return;
        }

        await using var world = new EndToEndWorld();
        world.CreateProjects();
        var sp = world.BuildServices();
        var catalog = sp.GetRequiredService<ICatalogService>();
        var runs = sp.GetRequiredService<IRunService>();
        await world.ConfigureAndScanAsync(catalog);
        var repo = EndToEndWorld.Item(catalog, world.GitRepoPath);
        var profile = catalog.GetProfiles(repo.Id).Single(p => p.IsDefault);

        // Workspace trust: nothing runs before the user trusts the folder.
        var untrusted = await Assert.ThrowsAsync<UntrustedItemException>(() => runs.StartAsync(repo.Id, profile, CancellationToken.None));
        Assert.Equal(repo.Id, untrusted.ItemId);
        Assert.Empty(runs.Running);
        Assert.Null(catalog.GetMeta(repo.Id).LastRunAt);

        await catalog.UpdateMetaAsync(repo.Id, m => m with { Trusted = true }, CancellationToken.None);
        var urlSeen = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = await runs.StartAsync(repo.Id, profile, CancellationToken.None);
        running.UrlDetected += (_, u) => urlSeen.TrySetResult(u);
        if (running.DetectedUrls.Count > 0)
        {
            urlSeen.TrySetResult(running.DetectedUrls[0]);
        }

        try
        {
            var url = await urlSeen.Task.WaitAsync(UrlTimeout);
            Assert.Equal(new Uri($"http://127.0.0.1:{world.Port}/"), url);
            Assert.Contains(running, runs.ForItem(repo.Id));
            Assert.False(running.HasExited);

            var rootPid = running.ProcessId;
            int? childPid = null;
            if (world.UsesPython)
            {
                Assert.True(await EndToEndWorld.WaitUntilAsync(() => ChildPid(running) is not null, UrlTimeout));
                childPid = ChildPid(running);
                Assert.False(EndToEndWorld.IsProcessGone(childPid!.Value));
            }

            await runs.StopAsync(running.RunId, CancellationToken.None);

            Assert.True(await EndToEndWorld.WaitUntilAsync(() => EndToEndWorld.IsProcessGone(rootPid), TimeSpan.FromSeconds(10)), "root process still alive");
            if (childPid is { } child)
            {
                Assert.True(await EndToEndWorld.WaitUntilAsync(() => EndToEndWorld.IsProcessGone(child), TimeSpan.FromSeconds(10)), "child process still alive");
            }

            Assert.Empty(runs.ForItem(repo.Id));
            var meta = catalog.GetMeta(repo.Id);
            Assert.True(meta.Trusted);
            Assert.NotNull(meta.LastRunAt);
        }
        finally
        {
            await runs.StopAllAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Uninstall_dirty_repo_needs_typed_name_then_recycles_folder_and_writes_audit()
    {
        if (!EndToEndWorld.HasGit)
        {
            return;
        }

        await using var world = new EndToEndWorld();
        world.CreateProjects();
        var sp = world.BuildServices();
        var catalog = sp.GetRequiredService<ICatalogService>();
        var uninstall = sp.GetRequiredService<IUninstallService>();
        await world.ConfigureAndScanAsync(catalog);
        var repo = EndToEndWorld.Item(catalog, world.GitRepoPath);
        Assert.True(catalog.State.Settings.UseRecycleBin);

        File.WriteAllText(Path.Combine(world.GitRepoPath, "uncommitted.txt"), "work in progress\n");

        var plan = await uninstall.PlanAsync(repo.Id, CancellationToken.None);
        Assert.False(plan.IsBlocked);
        Assert.True(plan.RequiresTypedConfirmation);
        Assert.Contains(plan.Warnings, w => w.Level == WarningLevel.Danger
            && (w.Message.Contains("1 untracked file", StringComparison.Ordinal) || w.Message.Contains("1 izlenmeyen dosya", StringComparison.Ordinal)));
        var remove = Assert.Single(plan.Steps, s => s.Kind == UninstallStepKind.RemoveDirectory);
        Assert.Equal(world.GitRepoPath, remove.Path, ignoreCase: true);
        Assert.Equal(UninstallStepKind.RemoveCatalogEntry, plan.Steps[^1].Kind);

        await Assert.ThrowsAsync<UninstallConfirmationException>(
            () => uninstall.ExecuteAsync(plan, plan.Steps, "demo-serve", null, CancellationToken.None));
        await Assert.ThrowsAsync<UninstallConfirmationException>(
            () => uninstall.ExecuteAsync(plan, plan.Steps, null, null, CancellationToken.None));
        Assert.True(Directory.Exists(world.GitRepoPath));
        Assert.Contains(catalog.State.Items, i => i.Id == repo.Id);
        Assert.False(File.Exists(world.AuditLogPath));

        Assert.Equal("demo-server", plan.ItemName);
        var result = await uninstall.ExecuteAsync(plan, plan.Steps, repo.Name, null, CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", result.Outcomes.Where(o => !o.Success).Select(o => o.Error)));
        Assert.False(Directory.Exists(world.GitRepoPath));
        Assert.DoesNotContain(catalog.State.Items, i => i.Id == repo.Id);
        Assert.False(catalog.State.Meta.ContainsKey(repo.Id));
        Assert.Contains(catalog.State.Items, i => i.Id == ItemIds.ForPath(world.NodePath));

        var lines = File.ReadAllLines(world.AuditLogPath);
        var entry = Assert.Single(lines);
        using var json = JsonDocument.Parse(entry);
        Assert.Equal("uninstall", json.RootElement.GetProperty("action").GetString());
        Assert.Equal(repo.Id, json.RootElement.GetProperty("itemId").GetString());
        Assert.Equal("success", json.RootElement.GetProperty("outcome").GetString());
        Assert.Contains(
            json.RootElement.GetProperty("steps").EnumerateArray(),
            s => s.GetProperty("kind").GetString() == nameof(UninstallStepKind.RemoveDirectory) && s.GetProperty("success").GetBoolean());

        // The removal is persisted, not just in memory.
        var reloaded = world.BuildServices().GetRequiredService<ICatalogService>();
        await reloaded.InitializeAsync(CancellationToken.None);
        Assert.DoesNotContain(reloaded.State.Items, i => i.Id == repo.Id);
    }

    [Fact]
    public async Task Cleanup_frees_node_modules_and_keeps_package_json()
    {
        if (!EndToEndWorld.HasGit)
        {
            return;
        }

        await using var world = new EndToEndWorld();
        world.CreateProjects();
        var sp = world.BuildServices();
        var catalog = sp.GetRequiredService<ICatalogService>();
        var cleanup = sp.GetRequiredService<ICleanupService>();
        await world.ConfigureAndScanAsync(catalog);
        var node = EndToEndWorld.Item(catalog, world.NodePath);
        var nodeModules = node.Artifacts.Single(a => Path.GetFileName(a.Path) == "node_modules");

        var freed = await cleanup.CleanArtifactsAsync(node.Id, [nodeModules], CancellationToken.None);

        Assert.True(freed >= 64 * 1024, $"freed {freed} bytes");
        Assert.False(Directory.Exists(Path.Combine(world.NodePath, "node_modules")));
        Assert.True(File.Exists(Path.Combine(world.NodePath, "package.json")));

        // The item is refreshed: still in the catalog, artifact gone.
        var refreshed = EndToEndWorld.Item(catalog, world.NodePath);
        Assert.DoesNotContain(refreshed.Artifacts, a => Path.GetFileName(a.Path) == "node_modules");

        var entry = Assert.Single(File.ReadAllLines(world.AuditLogPath));
        using var json = JsonDocument.Parse(entry);
        Assert.Equal("cleanup", json.RootElement.GetProperty("action").GetString());
        Assert.Equal("success", json.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task New_service_instance_over_same_catalog_file_loads_items_settings_and_meta()
    {
        if (!EndToEndWorld.HasGit)
        {
            return;
        }

        await using var world = new EndToEndWorld();
        world.CreateProjects();
        var first = world.BuildServices().GetRequiredService<ICatalogService>();
        await world.ConfigureAndScanAsync(first);
        var nodeId = ItemIds.ForPath(world.NodePath);
        var repoId = ItemIds.ForPath(world.GitRepoPath);
        await first.UpdateMetaAsync(nodeId, m => m with { Favorite = true, Note = "e2e note", Tags = ["web", "demo"] }, CancellationToken.None);
        await first.UpdateMetaAsync(repoId, m => m with { Trusted = true, DefaultProfileId = first.GetProfiles(repoId)[0].Id }, CancellationToken.None);

        var second = world.BuildServices().GetRequiredService<ICatalogService>();
        Assert.NotSame(first, second);
        await second.InitializeAsync(CancellationToken.None);

        Assert.Equal(
            first.State.Items.Select(i => i.Id).Order(StringComparer.Ordinal),
            second.State.Items.Select(i => i.Id).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { world.ScanRoot }, second.State.Settings.ScanRoots);
        Assert.False(second.State.Settings.IncludeInstalledApps);
        Assert.False(second.State.Settings.IncludePackageTools);
        Assert.NotNull(second.State.LastScanAt);

        var nodeMeta = second.GetMeta(nodeId);
        Assert.True(nodeMeta.Favorite);
        Assert.Equal("e2e note", nodeMeta.Note);
        Assert.Equal("web,demo", string.Join(",", nodeMeta.Tags));

        var repoMeta = second.GetMeta(repoId);
        Assert.True(repoMeta.Trusted);
        Assert.Equal(first.GetProfiles(repoId)[0].Id, repoMeta.DefaultProfileId);
        Assert.Equal(first.GetProfiles(repoId), second.GetProfiles(repoId), new ProfileIdComparer());
        var repo = EndToEndWorld.Item(second, world.GitRepoPath);
        Assert.Equal(ItemKind.GitRepository, repo.Kind);
        Assert.Equal("initial commit", repo.Git?.LastCommitSubject);

        // A rescan from the reloaded instance keeps the user metadata.
        await second.ScanAsync(null, CancellationToken.None);
        Assert.True(second.GetMeta(nodeId).Favorite);
        Assert.True(second.GetMeta(repoId).Trusted);
        Assert.Equal(2, second.State.Items.Count);
    }

    private static int? ChildPid(IRunningProcess running)
    {
        foreach (var line in running.Output)
        {
            const string prefix = "child-pid=";
            var text = line.Text.Trim();
            if (text.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(text.AsSpan(prefix.Length), out var pid))
            {
                return pid;
            }
        }

        return null;
    }

    private sealed class ProfileIdComparer : IEqualityComparer<RunProfile>
    {
        public bool Equals(RunProfile? x, RunProfile? y) => x?.Id == y?.Id && x?.IsDefault == y?.IsDefault && x?.Command.Display == y?.Command.Display;

        public int GetHashCode(RunProfile obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }
}
