using System.Diagnostics;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Execution;
using Tezgah.Core.Model;
using Tezgah.Core.Safety;

namespace Tezgah.Core.Tests.Execution;

/// <summary>A unique folder under %TEMP%\TezgahTests, removed with the link-safe permanent delete.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TezgahTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(params string[] parts)
    {
        var p = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    public string File(string relative, string content = "x")
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                FileRemover.DeleteTreePermanently(Path, isRoot: true, CancellationToken.None);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class Junction
{
    /// <summary>Creates a directory junction with mklink /J. Returns false when that is not possible on this machine.</summary>
    public static bool TryCreate(string link, string target)
    {
        var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "/d", "/c", "mklink", "/J", link, target })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        return Directory.Exists(link) && (new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0;
    }
}

internal sealed class FakeCatalogService : ICatalogService
{
    private readonly Dictionary<string, UserMeta> _meta = [];

    public FakeCatalogService(params CatalogItem[] items)
    {
        State = new CatalogState { Items = items, Settings = new TezgahSettings { UseRecycleBin = false } };
    }

    public event EventHandler? Changed;

    public CatalogState State { get; set; }

    public bool IsScanning => false;

    public List<string> Removed { get; } = [];

    public List<string> Refreshed { get; } = [];

    public Dictionary<string, List<RunProfile>> Profiles { get; } = [];

    public void SetMeta(string id, UserMeta meta) => _meta[id] = meta;

    public UserMeta GetMeta(string itemId) => _meta.TryGetValue(itemId, out var m) ? m : new UserMeta();

    public IReadOnlyList<RunProfile> GetProfiles(string itemId) => Profiles.TryGetValue(itemId, out var p) ? p : [];

    public Task UpdateMetaAsync(string itemId, Func<UserMeta, UserMeta> update, CancellationToken ct)
    {
        _meta[itemId] = update(GetMeta(itemId));
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task RemoveItemAsync(string itemId, CancellationToken ct)
    {
        Removed.Add(itemId);
        State = State with { Items = State.Items.Where(i => i.Id != itemId).ToList() };
        return Task.CompletedTask;
    }

    public Task RefreshItemAsync(string itemId, bool fetchGit, CancellationToken ct)
    {
        Refreshed.Add(itemId);
        return Task.CompletedTask;
    }

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task ScanAsync(IProgress<ScanProgress>? progress, CancellationToken ct) => Task.CompletedTask;

    public Task UpdateSettingsAsync(Func<TezgahSettings, TezgahSettings> update, CancellationToken ct)
    {
        State = State with { Settings = update(State.Settings) };
        return Task.CompletedTask;
    }

    public Task<CatalogItem?> AddFolderAsync(string path, CancellationToken ct) => Task.FromResult<CatalogItem?>(null);

    public Task<CatalogItem?> CloneAsync(string gitUrl, string targetParent, IProgress<string>? progress, CancellationToken ct) => Task.FromResult<CatalogItem?>(null);

    public bool IsForgotten(CatalogItem item) => false;
}

internal sealed class FakeGitClient(GitInfo? info) : IGitClient
{
    public List<(string Path, bool Fetch)> Calls { get; } = [];

    public GitInfo? Info { get; set; } = info;

    public Dictionary<string, GitInfo?> ByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Ignored { get; set; } = [];

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<GitInfo?> GetInfoAsync(string repoPath, bool fetch, CancellationToken ct)
    {
        Calls.Add((repoPath, fetch));
        return Task.FromResult(ByPath.TryGetValue(repoPath, out var specific) ? specific : Info);
    }

    public Task<IReadOnlyList<string>> GetIgnoredPathsAsync(string repoPath, CancellationToken ct) => Task.FromResult(Ignored);

    public Task<CommandResult> PullAsync(string repoPath, CancellationToken ct) => Task.FromResult(new CommandResult(0, "", "", false));
}

internal sealed class FakeCommandExecutor(int exitCode = 0) : ICommandExecutor
{
    public List<CommandSpec> Runs { get; } = [];

    public Func<CommandSpec, Task>? OnRun { get; set; }

    public async Task<CommandResult> RunAsync(CommandSpec spec, TimeSpan timeout, CancellationToken ct)
    {
        Runs.Add(spec);
        if (OnRun is not null)
        {
            await OnRun(spec);
        }

        return new CommandResult(exitCode, "out", exitCode == 0 ? "" : "boom", false);
    }

    public string? ResolveExecutable(string name) => name;
}

internal sealed class FakeRunService : IRunService
{
    public event EventHandler? RunningChanged;

    public IReadOnlyList<IRunningProcess> Running => [];

    public Task<IRunningProcess> StartAsync(string itemId, RunProfile profile, CancellationToken ct) => throw new NotSupportedException();

    public Task StopAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;

    public Task StopAllAsync(CancellationToken ct)
    {
        RunningChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public IReadOnlyList<IRunningProcess> ForItem(string itemId) => [];
}

internal sealed class FakeProcessFinder : IProcessFinder
{
    public IReadOnlyList<FoundProcess> FindUnder(string folder) => [];

    public bool TryKill(FoundProcess process, string folder) => true;
}

internal static class Specs
{
    public static string Cmd => System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");

    public static CommandSpec Command(string file, string workDir, params string[] args) =>
        new() { FileName = file, WorkingDirectory = workDir, Arguments = args };

    public static RunProfile Profile(string id, CommandSpec command) =>
        new() { Id = id, Label = id, Kind = RunProfileKind.Run, Command = command, Origin = "test" };

    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }
}
