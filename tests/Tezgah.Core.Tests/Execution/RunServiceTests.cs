using Tezgah.Core.Abstractions;
using Tezgah.Core.Execution;
using Tezgah.Core.Model;
using Tezgah.Core.Safety;

namespace Tezgah.Core.Tests.Execution;

public class RunServiceTests
{
    private static CatalogItem Item(string path) => new()
    {
        Id = ItemIds.ForPath(path),
        Kind = ItemKind.ProjectFolder,
        Source = Sources.FileSystem,
        Name = "proj",
        Path = path,
    };

    [Fact]
    public async Task Untrusted_item_is_refused()
    {
        using var dir = new TempDir();
        var item = Item(dir.Path);
        var catalog = new FakeCatalogService(item);
        var profile = Specs.Profile("echo", Specs.Command("cmd.exe", dir.Path, "/d", "/c", "echo hi"));
        catalog.Profiles[item.Id] = [profile];
        await using var service = new RunService(catalog, PathGuard.Default, new ProcessHost());

        var ex = await Assert.ThrowsAsync<UntrustedItemException>(() => service.StartAsync(item.Id, profile, CancellationToken.None));
        Assert.Equal(item.Id, ex.ItemId);
        Assert.Empty(service.Running);
    }

    [Fact]
    public async Task Unknown_profile_is_refused()
    {
        using var dir = new TempDir();
        var item = Item(dir.Path);
        var catalog = new FakeCatalogService(item);
        catalog.SetMeta(item.Id, new UserMeta { Trusted = true });
        await using var service = new RunService(catalog, PathGuard.Default, new ProcessHost());
        var forged = Specs.Profile("evil", Specs.Command("cmd.exe", dir.Path, "/d", "/c", "echo pwned"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(item.Id, forged, CancellationToken.None));
    }

    [Fact]
    public async Task Working_directory_in_system_folder_is_refused()
    {
        using var dir = new TempDir();
        var item = Item(dir.Path);
        var catalog = new FakeCatalogService(item);
        catalog.SetMeta(item.Id, new UserMeta { Trusted = true });
        var profile = Specs.Profile("sys", Specs.Command("cmd.exe", Environment.SystemDirectory, "/d", "/c", "echo hi"));
        catalog.Profiles[item.Id] = [profile];
        await using var service = new RunService(catalog, PathGuard.Default, new ProcessHost());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.StartAsync(item.Id, profile, CancellationToken.None));
    }

    [Fact]
    public async Task Trusted_item_runs_tracks_and_stops()
    {
        using var dir = new TempDir();
        var item = Item(dir.Path);
        var catalog = new FakeCatalogService(item);
        catalog.SetMeta(item.Id, new UserMeta { Trusted = true });
        var profile = Specs.Profile("ping", Specs.Command("ping.exe", dir.Path, "-n", "60", "127.0.0.1"));
        catalog.Profiles[item.Id] = [profile];
        await using var service = new RunService(catalog, PathGuard.Default, new ProcessHost());
        var changes = 0;
        service.RunningChanged += (_, _) => Interlocked.Increment(ref changes);

        // The caller passes a tampered command under a known id: the catalog's command is what runs.
        var tampered = profile with { Command = profile.Command with { Arguments = ["-n", "1", "10.255.255.1"] } };
        var run = await service.StartAsync(item.Id, tampered, CancellationToken.None);

        Assert.Equal(profile.Command.Arguments, run.Profile.Command.Arguments);
        Assert.Single(service.Running);
        Assert.Single(service.ForItem(item.Id));
        Assert.NotNull(catalog.GetMeta(item.Id).LastRunAt);

        await service.StopAllAsync(CancellationToken.None);
        Assert.Empty(service.Running);
        Assert.True(run.HasExited);

        // RunningChanged is raised on a background thread; the removal notification may land just after StopAllAsync.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref changes) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(Volatile.Read(ref changes) >= 2);
    }

    [Fact]
    public async Task Exited_processes_leave_the_running_list()
    {
        using var dir = new TempDir();
        var item = Item(dir.Path);
        var catalog = new FakeCatalogService(item);
        catalog.SetMeta(item.Id, new UserMeta { Trusted = true });
        var profile = Specs.Profile("echo", Specs.Command("cmd.exe", dir.Path, "/d", "/c", "echo done"));
        catalog.Profiles[item.Id] = [profile];
        await using var service = new RunService(catalog, PathGuard.Default, new ProcessHost());
        var run = await service.StartAsync(item.Id, profile, CancellationToken.None);
        Assert.True(await Specs.WaitUntilAsync(() => service.Running.Count == 0, TimeSpan.FromSeconds(15)));
        Assert.True(run.HasExited);
        Assert.Equal(0, run.ExitCode);
    }
}
