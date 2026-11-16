using Tezgah.Core.Execution;
using Tezgah.Core.Model;
using Tezgah.Core.Safety;
using Tezgah.Core.Tests.Execution;
using Tezgah.Core.Uninstall;

namespace Tezgah.Core.Tests.Uninstall;

public sealed class StopProcessesBindingTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private (UninstallService Service, CatalogItem Item, ScriptedProcessFinder Finder) Create()
    {
        var path = _dir.Sub("folders", "tool");
        File.WriteAllText(Path.Combine(path, "tool.exe"), "x");
        var item = new CatalogItem { Id = ItemIds.ForPath(path), Kind = ItemKind.ProjectFolder, Source = Sources.FileSystem, Name = "tool", Path = path };
        var finder = new ScriptedProcessFinder();
        var options = new UninstallOptions { AuditLogPath = Path.Combine(_dir.Path, "state", "audit.log") };
        var service = new UninstallService(new FakeCatalogService(item), new FakeGitClient(null), PathGuard.Default, new FileRemover(PathGuard.Default),
            new FakeCommandExecutor(0), new FakeRunService(), finder, options);
        return (service, item, finder);
    }

    [Fact]
    public async Task Stops_exactly_the_processes_shown_in_the_plan()
    {
        var (service, item, finder) = Create();
        var shown = new FoundProcess(4242, Path.Combine(item.Path!, "tool.exe"));
        finder.Running = [shown];

        var plan = await service.PlanAsync(item.Id, CancellationToken.None);
        Assert.Contains(plan.Warnings, w => w.Message.Contains("4242", StringComparison.Ordinal));
        var result = await service.ExecuteAsync(plan, plan.Steps, plan.RequiresTypedConfirmation ? item.Name : null, null, CancellationToken.None);

        Assert.Equal([shown], finder.Killed);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Process_started_after_planning_is_not_killed_and_refuses_the_removal()
    {
        var (service, item, finder) = Create();
        var shown = new FoundProcess(4242, Path.Combine(item.Path!, "tool.exe"));
        finder.Running = [shown];
        var plan = await service.PlanAsync(item.Id, CancellationToken.None);

        // Between the confirmation and execution the user starts another program from the folder.
        finder.Running = [shown, new FoundProcess(5151, Path.Combine(item.Path!, "editor.exe"))];
        var result = await service.ExecuteAsync(plan, plan.Steps, plan.RequiresTypedConfirmation ? item.Name : null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(finder.Killed);
        Assert.True(Directory.Exists(item.Path));
    }

    private sealed class ScriptedProcessFinder : IProcessFinder
    {
        public IReadOnlyList<FoundProcess> Running { get; set; } = [];

        public List<FoundProcess> Killed { get; } = [];

        public IReadOnlyList<FoundProcess> FindUnder(string folder) => Running;

        public bool TryKill(FoundProcess process, string folder)
        {
            Killed.Add(process);
            return true;
        }
    }
}
