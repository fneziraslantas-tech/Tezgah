using System.Text.Json;
using Tezgah.Core.Model;
using Tezgah.Core.Safety;
using Tezgah.Core.Tests.Execution;
using Tezgah.Core.Uninstall;

namespace Tezgah.Core.Tests.Uninstall;

public sealed class UninstallServiceTests : IDisposable
{
    private readonly TempDir _dir = new();

    private static readonly GitInfo CleanGit = new()
    {
        RemoteUrl = "https://github.com/x/y.git",
        Branch = "main",
        HasUpstream = true,
        LastCommitAt = DateTimeOffset.Now,
    };

    public void Dispose() => _dir.Dispose();

    private string AuditPath => Path.Combine(_dir.Path, "state", "audit.log");

    private (UninstallService Service, FakeCatalogService Catalog, FakeCommandExecutor Executor) Create(
        CatalogItem item, GitInfo? git = null, int exitCode = 0, PathGuardOptions? guardOptions = null)
    {
        var catalog = new FakeCatalogService(item);
        var executor = new FakeCommandExecutor(exitCode);
        var guard = guardOptions is null ? PathGuard.Default : new PathGuard(guardOptions);
        var options = new UninstallOptions
        {
            AuditLogPath = AuditPath,
            VirtualenvRoots = [Path.Combine(_dir.Path, "venvs")],
            ShortcutRootsRecursive = [Path.Combine(_dir.Path, "startmenu")],
            ShortcutRootsTopLevel = [Path.Combine(_dir.Path, "desktop")],
        };
        var service = new UninstallService(catalog, new FakeGitClient(git), guard, new FileRemover(guard), executor, new FakeRunService(), new FakeProcessFinder(), options);
        return (service, catalog, executor);
    }

    private CatalogItem Repo(string name = "my-repo", ItemKind kind = ItemKind.GitRepository)
    {
        var path = _dir.Sub("repos", name);
        File.WriteAllText(Path.Combine(path, "README.md"), "hello");
        return new CatalogItem { Id = ItemIds.ForPath(path), Kind = kind, Source = Sources.FileSystem, Name = name, Path = path };
    }

    [Fact]
    public async Task Unsaved_work_requires_typed_confirmation()
    {
        var item = Repo();
        var (service, catalog, _) = Create(item, CleanGit with { ModifiedFiles = 3, UntrackedFiles = 2, Stashes = 1, Ahead = 4 });

        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        Assert.True(plan.RequiresTypedConfirmation);
        Assert.False(plan.IsBlocked);
        var danger = Assert.Single(plan.Warnings, w => w.Level == WarningLevel.Danger);
        Assert.Contains("3", danger.Message, StringComparison.Ordinal);
        Assert.Contains("4", danger.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<UninstallConfirmationException>(() => service.ExecuteAsync(plan, plan.Steps, null, null, CancellationToken.None));
        await Assert.ThrowsAsync<UninstallConfirmationException>(() => service.ExecuteAsync(plan, plan.Steps, "MY-REPO", null, CancellationToken.None));
        Assert.True(Directory.Exists(item.Path));

        var progress = new List<string>();
        var result = await service.ExecuteAsync(plan, plan.Steps, "  my-repo ", new SyncProgress(progress), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(item.Path));
        Assert.Equal([item.Id], catalog.Removed);
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task No_remote_is_danger()
    {
        var item = Repo();
        var (service, _, _) = Create(item, CleanGit with { RemoteUrl = null, HasUpstream = false });
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);
        Assert.True(plan.RequiresTypedConfirmation);
        Assert.Contains(plan.Warnings, w => w.Level == WarningLevel.Danger && w.Message.Contains("remote", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unknown_git_state_is_danger()
    {
        var item = Repo();
        var (service, _, _) = Create(item, git: null);
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);
        Assert.True(plan.RequiresTypedConfirmation);
    }

    [Fact]
    public async Task Project_folder_without_source_is_danger()
    {
        var item = Repo("zip-download", ItemKind.ProjectFolder);
        var (service, _, _) = Create(item);
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);
        Assert.True(plan.RequiresTypedConfirmation);
    }

    [Fact]
    public async Task Clean_repo_runs_without_confirmation_and_writes_audit_log()
    {
        var item = Repo();
        var (service, catalog, _) = Create(item, CleanGit);
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        Assert.False(plan.RequiresTypedConfirmation);
        Assert.Contains(plan.Warnings, w => w.Level == WarningLevel.Info);
        Assert.Collection(
            plan.Steps,
            s => Assert.Equal(UninstallStepKind.StopProcesses, s.Kind),
            s => Assert.Equal(UninstallStepKind.RemoveDirectory, s.Kind),
            s => Assert.Equal(UninstallStepKind.RemoveCatalogEntry, s.Kind));
        Assert.True(plan.TotalBytes > 0);

        var result = await service.ExecuteAsync(plan, [], null, null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.False(Directory.Exists(item.Path));
        Assert.Single(catalog.Removed);

        var line = Assert.Single(File.ReadAllLines(AuditPath));
        using var json = JsonDocument.Parse(line);
        Assert.Equal(item.Id, json.RootElement.GetProperty("itemId").GetString());
        Assert.Equal("success", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task Plan_can_only_be_executed_once_and_must_not_be_forged()
    {
        var item = Repo();
        var (service, _, _) = Create(item, CleanGit);
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        var forged = plan with
        {
            Steps = [new UninstallStep { Kind = UninstallStepKind.RemoveDirectory, Path = _dir.Path, Description = "evil" }],
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(forged, forged.Steps, null, null, CancellationToken.None));
        Assert.True(Directory.Exists(_dir.Path));

        await service.ExecuteAsync(plan, [], null, null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(plan, [], null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Protected_path_blocks_the_plan()
    {
        var item = new CatalogItem
        {
            Id = "path:c:\\windows",
            Kind = ItemKind.GitRepository,
            Source = Sources.FileSystem,
            Name = "Windows",
            Path = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };
        var (service, catalog, _) = Create(item, CleanGit);
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        Assert.True(plan.IsBlocked);
        Assert.DoesNotContain(plan.Steps, s => s.Kind == UninstallStepKind.RemoveDirectory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(plan, plan.Steps, "Windows", null, CancellationToken.None));
        Assert.Empty(catalog.Removed);
    }

    [Fact]
    public async Task Related_virtualenv_is_optional_and_respected()
    {
        var item = Repo("my_proj");
        var venv = _dir.Sub("venvs", "my-proj-Ab12Cd34-py3.12");
        var unrelated = _dir.Sub("venvs", "other-proj-Zz99");
        File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"), "home = x");
        var pipenvOwned = _dir.Sub("venvs", "different-name-1234");
        File.WriteAllText(Path.Combine(pipenvOwned, ".project"), item.Path);

        var (service, _, _) = Create(item, CleanGit);
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        var venvSteps = plan.Steps.Where(s => s.Kind == UninstallStepKind.RemoveDirectory && s.Optional).ToList();
        Assert.Equal(2, venvSteps.Count);
        Assert.Contains(venvSteps, s => s.Path == venv);
        Assert.Contains(venvSteps, s => s.Path == pipenvOwned);

        // Untick the poetry venv; keep the pipenv one.
        var selected = venvSteps.Where(s => s.Path == pipenvOwned).ToList();
        var result = await service.ExecuteAsync(plan, selected, null, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(Directory.Exists(venv));
        Assert.False(Directory.Exists(pipenvOwned));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public async Task Shortcuts_pointing_into_the_item_are_removed()
    {
        var item = Repo();
        var inside = LnkBuilder.Write(Path.Combine(_dir.Path, "startmenu", "Tools", "MyRepo.lnk"), Path.Combine(item.Path!, "app.exe"));
        var outside = LnkBuilder.Write(Path.Combine(_dir.Path, "desktop", "Other.lnk"), @"C:\elsewhere\app.exe");

        var (service, _, _) = Create(item, CleanGit);
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        var shortcut = Assert.Single(plan.Steps, s => s.Kind == UninstallStepKind.RemoveShortcut);
        Assert.Equal(inside, shortcut.Path);

        await service.ExecuteAsync(plan, plan.Steps, null, null, CancellationToken.None);
        Assert.False(File.Exists(inside));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task Installed_app_runs_uninstaller_then_removes_leftovers()
    {
        var programFiles = _dir.Sub("PF");
        var install = _dir.Sub("PF", "Vendor App");
        File.WriteAllText(Path.Combine(install, "leftover.log"), "x");
        var uninstall = new CommandSpec { FileName = Path.Combine(install, "unins000.exe"), Arguments = ["/SILENT"], WorkingDirectory = install };
        var item = new CatalogItem
        {
            Id = "registry:vendor-app",
            Kind = ItemKind.InstalledApp,
            Source = Sources.Registry,
            Name = "Vendor App",
            Path = install,
            UninstallCommand = uninstall,
        };
        var guardOptions = PathGuardOptions.FromEnvironment() with { InstallRoots = [programFiles] };
        var (service, catalog, executor) = Create(item, guardOptions: guardOptions);

        var plan = await service.PlanAsync(item.Id, CancellationToken.None);
        Assert.Contains(plan.Warnings, w => w.Level == WarningLevel.Info);
        Assert.False(plan.RequiresTypedConfirmation);
        Assert.Contains(plan.Steps, s => s.Kind == UninstallStepKind.RunCommand && s.Command == uninstall);
        Assert.Contains(plan.Steps, s => s.Kind == UninstallStepKind.RemoveDirectory && s.Optional && s.Path == install);

        var result = await service.ExecuteAsync(plan, plan.Steps, null, null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Single(executor.Runs);
        Assert.False(Directory.Exists(install));
        Assert.True(Directory.Exists(programFiles));
        Assert.Single(catalog.Removed);
    }

    [Fact]
    public async Task Installed_app_leftovers_already_gone_is_success()
    {
        var install = _dir.Sub("PF", "Gone App");
        var item = new CatalogItem
        {
            Id = "registry:gone",
            Kind = ItemKind.InstalledApp,
            Source = Sources.Registry,
            Name = "Gone App",
            Path = install,
            UninstallCommand = new CommandSpec { FileName = "msiexec.exe", Arguments = ["/x", "{GUID}"], WorkingDirectory = install },
        };
        var (service, _, executor) = Create(item, guardOptions: PathGuardOptions.FromEnvironment() with { InstallRoots = [_dir.Sub("PF")] });
        executor.OnRun = _ =>
        {
            FileRemover.DeleteTreePermanently(install, true, CancellationToken.None); // the vendor uninstaller removed it
            return Task.CompletedTask;
        };

        var plan = await service.PlanAsync(item.Id, CancellationToken.None);
        var result = await service.ExecuteAsync(plan, plan.Steps, null, null, CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Failing_mandatory_step_stops_execution()
    {
        var install = _dir.Sub("PF2", "Broken");
        var item = new CatalogItem
        {
            Id = "registry:broken",
            Kind = ItemKind.InstalledApp,
            Source = Sources.Registry,
            Name = "Broken",
            Path = install,
            UninstallCommand = new CommandSpec { FileName = "unins.exe", WorkingDirectory = install },
        };
        var (service, catalog, _) = Create(item, exitCode: 1603, guardOptions: PathGuardOptions.FromEnvironment() with { InstallRoots = [_dir.Sub("PF2")] });
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);
        var result = await service.ExecuteAsync(plan, plan.Steps, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Outcomes, o => !o.Success && o.Error!.Contains("1603", StringComparison.Ordinal));
        Assert.Empty(catalog.Removed);
        Assert.True(Directory.Exists(install));
        Assert.Contains("\"outcome\":\"failed\"", File.ReadAllText(AuditPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Installed_app_without_uninstaller_is_blocked()
    {
        var item = new CatalogItem { Id = "registry:none", Kind = ItemKind.InstalledApp, Source = Sources.Registry, Name = "None" };
        var (service, _, _) = Create(item);
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);
        Assert.True(plan.IsBlocked);
    }

    [Fact]
    public async Task Go_binary_is_deleted_as_a_file()
    {
        var bin = _dir.File(@"go\bin\mytool.exe", "MZ");
        var item = new CatalogItem { Id = "go:mytool", Kind = ItemKind.PackageTool, Source = Sources.GoBin, Name = "mytool", Path = bin };
        var (service, catalog, _) = Create(item);
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);
        Assert.False(plan.IsBlocked);
        var result = await service.ExecuteAsync(plan, plan.Steps, null, null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.False(File.Exists(bin));
        Assert.True(Directory.Exists(Path.GetDirectoryName(bin)));
        Assert.Single(catalog.Removed);
    }

    [Fact]
    public async Task Package_tool_runs_manager_command()
    {
        var item = new CatalogItem
        {
            Id = "pipx:black",
            Kind = ItemKind.PackageTool,
            Source = Sources.Pipx,
            Name = "black",
            UninstallCommand = new CommandSpec { FileName = "pipx", Arguments = ["uninstall", "black"], WorkingDirectory = _dir.Path },
        };
        var (service, _, executor) = Create(item);
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);
        await service.ExecuteAsync(plan, plan.Steps, null, null, CancellationToken.None);
        Assert.Equal(["uninstall", "black"], Assert.Single(executor.Runs).Arguments);
    }

    private sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }
}
