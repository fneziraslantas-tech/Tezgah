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

    private FakeGitClient Git { get; set; } = new(null);

    private (UninstallService Service, FakeCatalogService Catalog, FakeCommandExecutor Executor) Create(
        CatalogItem item, GitInfo? git = null, int exitCode = 0, PathGuardOptions? guardOptions = null, params CatalogItem[] others)
    {
        var catalog = new FakeCatalogService([item, .. others]);
        var executor = new FakeCommandExecutor(exitCode);
        var guard = guardOptions is null ? PathGuard.Default : new PathGuard(guardOptions);
        var options = new UninstallOptions
        {
            AuditLogPath = AuditPath,
            VirtualenvRoots = [Path.Combine(_dir.Path, "venvs")],
            ShortcutRootsRecursive = [Path.Combine(_dir.Path, "startmenu")],
            ShortcutRootsTopLevel = [Path.Combine(_dir.Path, "desktop")],
        };
        Git = new FakeGitClient(git);
        var service = new UninstallService(catalog, Git, guard, new FileRemover(guard), executor, new FakeRunService(), new FakeProcessFinder(), options);
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
    public async Task Unpushed_commits_on_other_branches_require_typed_confirmation()
    {
        var item = Repo();
        var (service, _, _) = Create(item, CleanGit with { UnpushedCommits = 15, UnpushedBranches = ["feature/x"] });

        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        Assert.True(plan.RequiresTypedConfirmation);
        Assert.Contains(plan.Warnings, w => w.Level == WarningLevel.Danger && w.Message.Contains("feature/x", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_branch_state_fails_closed()
    {
        var item = Repo();
        var (service, _, _) = Create(item, CleanGit with { UnpushedCommits = null });
        Assert.True((await service.PlanAsync(item.Id, CancellationToken.None)).RequiresTypedConfirmation);
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

        var lines = File.ReadAllLines(AuditPath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"outcome\":\"started\"", lines[0], StringComparison.Ordinal); // intent written before anything was deleted
        using var json = JsonDocument.Parse(lines[1]);
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
        var venv = _dir.Sub("venvs", $"my-proj-{UninstallService.PoetryPathHash(item.Path!)}-py3.12");
        var unrelated = _dir.Sub("venvs", "other-proj-Zz99");
        var sameNameOtherProject = _dir.Sub("venvs", "my-proj-Ab12Cd34-py3.12"); // Poetry venv of another "my-proj" folder
        var prefixOnly = _dir.Sub("venvs", "my_proj-legacy");
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
        Assert.True(Directory.Exists(sameNameOtherProject));
        Assert.True(Directory.Exists(prefixOnly));
    }

    [Fact]
    public async Task Name_only_virtualenv_is_offered_unselected_with_a_warning()
    {
        var item = Repo("api");
        var wrapper = _dir.Sub("venvs", "api"); // virtualenvwrapper env without a .project owner file
        var (service, _, _) = Create(item, CleanGit);

        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        var step = Assert.Single(plan.Steps, s => s.Path == wrapper);
        Assert.True(step.Optional);
        Assert.False(step.DefaultSelected);
        Assert.Contains(plan.Warnings, w => w.Level == WarningLevel.Warning && w.Message.Contains("api", StringComparison.Ordinal));
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
        Assert.True(plan.RequiresTypedConfirmation); // Recycle Bin is off in the fake catalog: leftovers would be permanent
        var run = Assert.Single(plan.Steps, s => s.Kind == UninstallStepKind.RunCommand);
        Assert.Equal(uninstall with { WaitForProcessTree = true }, run.Command);
        var leftovers = Assert.Single(plan.Steps, s => s.Kind == UninstallStepKind.RemoveDirectory && s.Optional && s.Path == install);
        Assert.False(leftovers.DefaultSelected); // opt-in
        Assert.NotNull(leftovers.SizeBytes);

        var result = await service.ExecuteAsync(plan, plan.Steps, "Vendor App", null, CancellationToken.None);
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
        var result = await service.ExecuteAsync(plan, plan.Steps, "Gone App", null, CancellationToken.None);
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
        var result = await service.ExecuteAsync(plan, plan.Steps, "Broken", null, CancellationToken.None);

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

    private (CatalogItem Item, string Root, string Install) InstalledApp(string name, string? location = null, string? uninstaller = null)
    {
        var root = _dir.Sub("PFX");
        var install = location ?? _dir.Sub("PFX", name);
        File.WriteAllText(Path.Combine(install, "app.exe"), "MZ");
        var item = new CatalogItem
        {
            Id = "registry:" + name.ToLowerInvariant(),
            Kind = ItemKind.InstalledApp,
            Source = Sources.Registry,
            Name = name,
            Path = install,
            UninstallCommand = new CommandSpec { FileName = uninstaller ?? "msiexec.exe", Arguments = ["/x", "{GUID}"], WorkingDirectory = install },
        };
        return (item, root, install);
    }

    [Fact]
    public async Task Database_cluster_kept_by_the_uninstaller_is_never_swept_as_leftovers()
    {
        var (item, root, install) = InstalledApp("PostgreSQL 17");
        var (service, catalog, executor) = Create(item, guardOptions: PathGuardOptions.FromEnvironment() with { InstallRoots = [root] });
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);
        var leftovers = Assert.Single(plan.Steps, s => s.Kind == UninstallStepKind.RemoveDirectory);

        // The vendor uninstaller deliberately leaves the data directory behind.
        executor.OnRun = _ =>
        {
            Directory.CreateDirectory(Path.Combine(install, "data"));
            File.WriteAllText(Path.Combine(install, "data", "PG_VERSION"), "17");
            return Task.CompletedTask;
        };
        var result = await service.ExecuteAsync(plan, plan.Steps, item.Name, null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(install, "data", "PG_VERSION")));
        Assert.Contains(result.Outcomes, o => o.Step == leftovers && !o.Success);
        Assert.Single(catalog.Removed); // the uninstall itself succeeded
    }

    [Fact]
    public async Task Install_folder_with_data_is_not_offered_at_all()
    {
        var (item, root, install) = InstalledApp("Db Server");
        File.WriteAllText(Path.Combine(_dir.Sub("PFX", "Db Server", "data"), "PG_VERSION"), "17");
        var (service, _, _) = Create(item, guardOptions: PathGuardOptions.FromEnvironment() with { InstallRoots = [root] });

        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        Assert.DoesNotContain(plan.Steps, s => s.Kind is UninstallStepKind.RemoveDirectory or UninstallStepKind.StopProcesses);
        Assert.Contains(plan.Warnings, w => w.Level == WarningLevel.Warning && w.Message.Contains(install, StringComparison.Ordinal));
        Assert.True(Directory.Exists(install));
    }

    [Theory]
    [InlineData("Common Files")]
    [InlineData("dotnet")]
    [InlineData("Microsoft Visual Studio")]
    [InlineData("Windows Defender")]
    public async Task Shared_vendor_folders_are_never_leftovers(string shared)
    {
        var root = _dir.Sub("PFS");
        var location = _dir.Sub("PFS", shared);
        var (item, _, _) = InstalledApp("Runtime", location);
        var (service, _, _) = Create(item, guardOptions: PathGuardOptions.FromEnvironment() with { InstallRoots = [root] });

        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        Assert.DoesNotContain(plan.Steps, s => s.Kind == UninstallStepKind.RemoveDirectory);
    }

    [Fact]
    public async Task Install_folder_shared_with_another_entry_is_not_offered()
    {
        var (item, root, install) = InstalledApp("Suite A");
        var other = new CatalogItem
        {
            Id = "registry:suite-b",
            Kind = ItemKind.InstalledApp,
            Source = Sources.Registry,
            Name = "Suite B",
            Path = Path.Combine(install, "B"),
            UninstallCommand = new CommandSpec { FileName = Path.Combine(install, "B", "unins.exe"), WorkingDirectory = install },
        };
        var (service, _, _) = Create(item, guardOptions: PathGuardOptions.FromEnvironment() with { InstallRoots = [root] }, others: other);

        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        Assert.DoesNotContain(plan.Steps, s => s.Kind == UninstallStepKind.RemoveDirectory);
        Assert.Contains(plan.Warnings, w => w.Message.Contains("Suite B", StringComparison.Ordinal));
    }

    [Fact]
    public async Task User_folder_claimed_as_install_location_is_never_touched()
    {
        var project = _dir.Sub("source", "repos", "work-project");
        var (item, _, _) = InstalledApp("Sloppy", project); // uninstaller (msiexec) lives elsewhere, folder not under an install root
        var (service, _, _) = Create(item, guardOptions: PathGuardOptions.FromEnvironment() with { InstallRoots = [_dir.Sub("PFX")] });

        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        Assert.DoesNotContain(plan.Steps, s => s.Kind is UninstallStepKind.RemoveDirectory or UninstallStepKind.StopProcesses);
    }

    [Fact]
    public async Task Uninstaller_that_is_still_present_means_not_finished()
    {
        var root = _dir.Sub("PFY");
        var install = _dir.Sub("PFY", "Electron App");
        var exe = Path.Combine(install, "Uninstall Electron App.exe");
        File.WriteAllText(exe, "MZ");
        var (item, _, _) = InstalledApp("Electron App", install, exe);
        var (service, catalog, _) = Create(item, guardOptions: PathGuardOptions.FromEnvironment() with { InstallRoots = [root] });
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        var result = await service.ExecuteAsync(plan, plan.Steps, item.Name, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(Directory.Exists(install));
        Assert.Empty(catalog.Removed);
    }

    [Fact]
    public async Task Nested_repository_with_unsaved_work_makes_a_project_folder_danger()
    {
        var item = Repo("tools", ItemKind.ProjectFolder) with { WebUrl = "https://example.com/tools" };
        var api = _dir.Sub("repos", "tools", "api");
        Directory.CreateDirectory(Path.Combine(api, ".git"));
        var web = _dir.Sub("repos", "tools", "web");
        Directory.CreateDirectory(Path.Combine(web, ".git"));
        var (service, _, _) = Create(item, CleanGit);
        Git.ByPath[api] = CleanGit with { ModifiedFiles = 2, Ahead = 1 };

        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        Assert.True(plan.RequiresTypedConfirmation);
        Assert.Contains(plan.Warnings, w => w.Level == WarningLevel.Danger && w.Message.Contains("'api'", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Level == WarningLevel.Info && w.Message.Contains("web", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ignored_nested_repository_inside_clean_repo_is_checked()
    {
        var item = Repo();
        var nested = _dir.Sub("repos", "my-repo", "vendor", "lib");
        File.WriteAllText(Path.Combine(nested, ".git"), "gitdir: ../../.git/modules/lib\n");
        var (service, _, _) = Create(item, CleanGit);
        Git.ByPath[nested] = CleanGit with { RemoteUrl = null };

        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        Assert.Contains(plan.Warnings, w => w.Level == WarningLevel.Danger && w.Message.Contains("lib", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Junction_inside_the_folder_is_not_followed_for_nested_repositories()
    {
        var item = Repo();
        var elsewhere = _dir.Sub("elsewhere", "repo");
        Directory.CreateDirectory(Path.Combine(elsewhere, ".git"));
        if (!Tezgah.Core.Tests.Discovery.TestTools.TryCreateJunction(Path.Combine(item.Path!, "link"), Path.Combine(_dir.Path, "elsewhere")))
        {
            return;
        }

        var (service, _, _) = Create(item, CleanGit);
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        Assert.DoesNotContain(Git.Calls, c => c.Path.Contains("elsewhere", StringComparison.OrdinalIgnoreCase) || c.Path.Contains("link", StringComparison.OrdinalIgnoreCase));
        Assert.False(plan.RequiresTypedConfirmation);
    }

    [Fact]
    public void Benign_reparse_folders_are_searched_for_nested_repositories()
    {
        // A OneDrive placeholder folder is a reparse point with a benign tag: FileRemover recurses into it and deletes what
        // is inside, so the nested-repository search must look inside too. A junction stands in for the placeholder; the
        // link classifier is the one the deletion uses, here told that this reparse point is benign.
        var item = Repo();
        var elsewhere = _dir.Sub("cloud", "repo");
        Directory.CreateDirectory(Path.Combine(elsewhere, ".git"));
        var placeholder = Path.Combine(item.Path!, "OneDriveFolder");
        if (!Tezgah.Core.Tests.Discovery.TestTools.TryCreateJunction(placeholder, Path.Combine(_dir.Path, "cloud")))
        {
            return;
        }

        var (service, _, _) = Create(item, CleanGit);

        var benign = service.FindNestedRepositories(item.Path!, CancellationToken.None, out var incomplete, isLinkLike: _ => false);
        Assert.Contains(Path.Combine(placeholder, "repo"), benign, StringComparer.OrdinalIgnoreCase);
        Assert.False(incomplete);

        // The shared default classifier treats a real junction as a link (removed as a link, target untouched): not searched.
        var linked = service.FindNestedRepositories(item.Path!, CancellationToken.None, out _);
        Assert.DoesNotContain(linked, p => p.StartsWith(placeholder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Data_marker_search_inside_benign_reparse_folders_and_fails_closed_at_its_bounds()
    {
        var folder = _dir.Sub("app");
        _dir.Sub("cloudtarget", "db");
        var placeholder = Path.Combine(folder, "Synced");
        if (Tezgah.Core.Tests.Discovery.TestTools.TryCreateJunction(placeholder, Path.Combine(_dir.Path, "cloudtarget")))
        {
            Assert.Equal(Path.Combine(placeholder, "db"), UninstallService.FindDataMarker(folder, CancellationToken.None, isLinkLike: _ => false), ignoreCase: true);
        }

        // Too deep to look everywhere and nothing found: "not fully checked" (never "no user data").
        var deep = _dir.Sub(["deep", .. Enumerable.Range(0, 10).Select(i => $"d{i}")]);
        File.WriteAllText(Path.Combine(deep, "notes.txt"), "x");
        var marker = UninstallService.FindDataMarker(Path.Combine(_dir.Path, "deep"), CancellationToken.None);
        Assert.NotNull(marker);

        var shallow = _dir.Sub("shallow", "a");
        File.WriteAllText(Path.Combine(shallow, "x.dll"), "x");
        Assert.Null(UninstallService.FindDataMarker(Path.Combine(_dir.Path, "shallow"), CancellationToken.None));
    }

    [Fact]
    public async Task Work_created_after_planning_refuses_execution()
    {
        var item = Repo();
        var (service, catalog, _) = Create(item, CleanGit);
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);
        Assert.False(plan.RequiresTypedConfirmation);

        Git.Info = CleanGit with { ModifiedFiles = 1 }; // the user edited a file while the dialog was open

        var ex = await Assert.ThrowsAsync<UninstallConfirmationException>(() => service.ExecuteAsync(plan, plan.Steps, null, null, CancellationToken.None));
        Assert.True(ex.PlanChanged);
        Assert.True(Directory.Exists(item.Path));
        Assert.Empty(catalog.Removed);

        // dirty at plan, clean at execute: the typed confirmation still works
        var again = await service.PlanAsync(item.Id, CancellationToken.None);
        Git.Info = CleanGit;
        Assert.True((await service.ExecuteAsync(again, again.Steps, item.Name, null, CancellationToken.None)).Success);
    }

    [Fact]
    public async Task Plan_contents_cannot_be_edited_after_planning()
    {
        var item = Repo();
        var (service, _, _) = Create(item, CleanGit with { RemoteUrl = null });
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);
        Assert.True(plan.RequiresTypedConfirmation);

        Assert.Throws<InvalidCastException>(() => ((List<PlanWarning>)plan.Warnings).Clear());
        Assert.Throws<InvalidCastException>(() => ((List<UninstallStep>)plan.Steps).Clear());
        await Assert.ThrowsAsync<UninstallConfirmationException>(() => service.ExecuteAsync(plan, plan.Steps, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Ignored_secrets_are_reported_and_danger_without_recycle_bin()
    {
        var item = Repo();
        var (service, _, _) = Create(item, CleanGit);
        Git.Ignored = [".env", "node_modules/", "bin/", "data/dev.sqlite", "notes.txt", ".env.example"];

        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        var danger = Assert.Single(plan.Warnings, w => w.Level == WarningLevel.Danger);
        Assert.Contains(".env", danger.Message, StringComparison.Ordinal);
        Assert.Contains("dev.sqlite", danger.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("node_modules", string.Join("|", plan.Warnings.Select(w => w.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public void Ignored_paths_are_classified()
    {
        var (sensitive, other) = UninstallService.ClassifyIgnored(["node_modules/", "src/bin/", "obj/", ".env", "x/.env.local", "notes.txt", "a/b.pyc", ".env.sample"]);
        Assert.Equal([".env", "x/.env.local"], sensitive);
        Assert.Equal(["notes.txt", ".env.sample"], other);
    }

    [Fact]
    public async Task Unwritable_audit_log_refuses_before_anything_is_deleted()
    {
        var item = Repo();
        File.WriteAllText(Path.Combine(_dir.Path, "state"), "a file where the audit folder should be");
        var (service, catalog, _) = Create(item, CleanGit);
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(plan, plan.Steps, null, null, CancellationToken.None));

        Assert.True(Directory.Exists(item.Path));
        Assert.Empty(catalog.Removed);
    }

    [Theory]
    [InlineData(@"C:\dev\proj\..\other\run.exe", null)]
    [InlineData(@"C:\dev\proj\.\bin\app.exe", null)]
    [InlineData(@"\\server\share\app.exe", null)]
    [InlineData("relative\\app.exe", null)]
    [InlineData(@"C:\dev\proj\bin\app.exe", @"C:\dev\proj\bin\app.exe")]
    public void Shortcut_targets_are_canonicalised(string raw, string? expected) =>
        Assert.Equal(expected, UninstallService.CanonicalShortcutTarget(raw));

    [Fact]
    public async Task Dotdot_shortcut_pointing_out_of_the_item_is_not_removed()
    {
        var item = Repo();
        var sneaky = LnkBuilder.Write(Path.Combine(_dir.Path, "startmenu", "Other.lnk"), Path.Combine(item.Path!, "..", "othertool", "run.exe"));
        var (service, _, _) = Create(item, CleanGit);
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);
        Assert.DoesNotContain(plan.Steps, s => s.Path == sneaky);
    }

    private sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }
}
