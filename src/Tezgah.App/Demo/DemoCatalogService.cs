using System.IO;
using Tezgah.Core;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.App.Demo;

/// <summary>In-memory catalog with sample data. Nothing touches the disk.</summary>
public sealed class DemoCatalogService : ICatalogService
{
    private readonly object _gate = new();
    private readonly bool _startEmpty;
    private readonly HashSet<string> _removedIds = [];
    private CatalogState _state = new();

    public DemoCatalogService(DemoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _startEmpty = options.StartEmpty;
    }

    public CatalogState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool IsScanning { get; private set; }

    public event EventHandler? Changed;

    public Task InitializeAsync(CancellationToken ct)
    {
        if (!_startEmpty)
        {
            var (items, meta) = DemoData.Create(DateTimeOffset.Now);
            Mutate(s => s with { Items = items, Meta = meta, LastScanAt = DateTimeOffset.Now.AddHours(-5), Settings = DefaultSettings() });
        }
        else
        {
            Mutate(s => s with { Settings = DefaultSettings() });
        }

        return Task.CompletedTask;
    }

    private static TezgahSettings DefaultSettings() => new()
    {
        ScanRoots = [DemoData.Repos, Path.Combine(DemoData.Home, "Desktop"), Path.Combine(DemoData.Home, "Downloads"), Path.Combine(DemoData.Home, "AI")],
    };

    public async Task ScanAsync(IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        IsScanning = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var (items, meta) = DemoData.Create(DateTimeOffset.Now);
            var found = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(90, ct).ConfigureAwait(false);
                found++;
                var stage = item.Kind switch
                {
                    ItemKind.InstalledApp => "registry",
                    ItemKind.PackageTool => item.Source,
                    _ => "fs",
                };
                progress?.Report(new ScanProgress(stage, item.Path, found));
            }

            Mutate(s =>
            {
                // keep user metadata the user changed during the demo, add sample metadata for new ids
                var mergedMeta = new Dictionary<string, UserMeta>(meta);
                foreach (var kv in s.Meta)
                {
                    mergedMeta[kv.Key] = kv.Value;
                }

                // items the user removed during the demo stay removed; manually added / cloned ones stay
                var extra = s.Items.Where(x => !items.Any(i => i.Id == x.Id));
                return s with
                {
                    Items = [.. items.Where(i => !_removedIds.Contains(i.Id)), .. extra],
                    Meta = mergedMeta,
                    LastScanAt = DateTimeOffset.Now,
                };
            });
        }
        finally
        {
            IsScanning = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RefreshItemAsync(string itemId, bool fetchGit, CancellationToken ct)
    {
        await Task.Delay(fetchGit ? 400 : 100, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task UpdateMetaAsync(string itemId, Func<UserMeta, UserMeta> update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        Mutate(s =>
        {
            var meta = new Dictionary<string, UserMeta>(s.Meta);
            meta[itemId] = update(meta.GetValueOrDefault(itemId) ?? new UserMeta());
            return s with { Meta = meta };
        });
        return Task.CompletedTask;
    }

    public Task UpdateSettingsAsync(Func<TezgahSettings, TezgahSettings> update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        Mutate(s => s with { Settings = update(s.Settings) });
        return Task.CompletedTask;
    }

    public Task RemoveItemAsync(string itemId, CancellationToken ct)
    {
        lock (_gate)
        {
            _removedIds.Add(itemId);
        }

        Mutate(s => s with { Items = [.. s.Items.Where(i => i.Id != itemId)] });
        return Task.CompletedTask;
    }

    public async Task<CatalogItem?> AddFolderAsync(string path, CancellationToken ct)
    {
        await Task.Delay(150, ct).ConfigureAwait(false);
        var full = Path.GetFullPath(path);
        var item = new CatalogItem
        {
            Id = ItemIds.ForPath(full),
            Kind = Directory.Exists(Path.Combine(full, ".git")) ? ItemKind.GitRepository : ItemKind.ProjectFolder,
            Source = Sources.FileSystem,
            Name = Path.GetFileName(full),
            Path = full,
            LastActivity = DateTimeOffset.Now,
            Description = L.T("Elle eklenen klasör (demo).", "Manually added folder (demo)."),
        };
        Mutate(s => s with { Items = [.. s.Items.Where(i => i.Id != item.Id), item] });
        return item;
    }

    public async Task<CatalogItem?> CloneAsync(string gitUrl, string targetParent, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitUrl);
        var name = gitUrl.TrimEnd('/').Split('/', ':').Last();
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        var path = Path.Combine(targetParent, name);
        string[] lines =
        [
            $"Cloning into '{name}'...",
            "remote: Enumerating objects: 1532, done.",
            "remote: Counting objects: 100% (412/412), done.",
            "Receiving objects:  48% (736/1532), 2.10 MiB | 4.20 MiB/s",
            "Receiving objects: 100% (1532/1532), 4.71 MiB | 4.40 MiB/s, done.",
            "Resolving deltas: 100% (803/803), done.",
        ];
        foreach (var line in lines)
        {
            await Task.Delay(300, ct).ConfigureAwait(false);
            progress?.Report(line);
        }

        var item = new CatalogItem
        {
            Id = ItemIds.ForPath(path),
            Kind = ItemKind.GitRepository,
            Source = Sources.FileSystem,
            Name = name,
            Path = path,
            Description = L.T("Az önce klonlandı (demo).", "Just cloned (demo)."),
            LastActivity = DateTimeOffset.Now,
            SizeBytes = 5 * 1024 * 1024,
            ProjectTypes = ["node"],
            RunProfiles = DemoData.NodeProfiles(path, "dev"),
            Git = new GitInfo { RemoteUrl = gitUrl, Branch = "main", HasUpstream = true, LastCommitAt = DateTimeOffset.Now.AddDays(-1), LastCommitSubject = "Initial commit" },
        };
        Mutate(s =>
        {
            var meta = new Dictionary<string, UserMeta>(s.Meta)
            {
                [item.Id] = (s.Meta.GetValueOrDefault(item.Id) ?? new UserMeta()) with { Trusted = false, ClonedFrom = gitUrl },
            };
            return s with { Items = [.. s.Items.Where(i => i.Id != item.Id), item], Meta = meta };
        });
        return item;
    }

    public UserMeta GetMeta(string itemId) => State.Meta.GetValueOrDefault(itemId) ?? new UserMeta();

    public IReadOnlyList<RunProfile> GetProfiles(string itemId)
    {
        var item = State.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            return [];
        }

        var meta = GetMeta(itemId);
        var profiles = item.RunProfiles.Concat(meta.CustomProfiles).ToList();
        if (meta.DefaultProfileId is { } def && profiles.Any(p => p.Id == def))
        {
            profiles = [.. profiles.Select(p => p with { IsDefault = p.Id == def })];
        }

        return profiles;
    }

    public bool IsForgotten(CatalogItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind is ItemKind.InstalledApp || item.LastActivity is null)
        {
            return false;
        }

        var meta = GetMeta(item.Id);
        var last = meta.LastRunAt is { } run && run > item.LastActivity ? run : item.LastActivity.Value;
        return (DateTimeOffset.Now - last).TotalDays > Math.Clamp(State.Settings.ForgottenAfterDays, 1, 3650);
    }

    /// <summary>Demo-only hook used by the fake cleanup service.</summary>
    internal void ReplaceItem(CatalogItem item) =>
        Mutate(s => s with { Items = [.. s.Items.Select(i => i.Id == item.Id ? item : i)] });

    private void Mutate(Func<CatalogState, CatalogState> change)
    {
        lock (_gate)
        {
            _state = change(_state);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record DemoOptions(bool StartEmpty);
