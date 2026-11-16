using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Discovery;
using Tezgah.Core.Model;

namespace Tezgah.Core.Catalog;

/// <summary>
/// Owns the in-memory catalog. State is an immutable snapshot swapped under a lock; all mutations persist through
/// <see cref="ICatalogStore"/> and raise <see cref="Changed"/> (outside the lock, on the calling thread).
/// </summary>
internal sealed class CatalogService : ICatalogService, IDisposable
{
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(30);
    private static readonly HashSet<string> PackageSources = new(StringComparer.Ordinal)
    {
        Sources.Pipx, Sources.NpmGlobal, Sources.Cargo, Sources.DotnetTool, Sources.Scoop, Sources.GoBin,
    };

    private readonly IReadOnlyList<IDiscoveryProvider> _providers;
    private readonly ICatalogStore _store;
    private readonly IGitClient _git;
    private readonly ProjectInspector _inspector;
    private readonly ILogger<CatalogService> _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CatalogState _state = new();
    private int _scanning;
    private bool _initialized;

    public CatalogService(
        IEnumerable<IDiscoveryProvider> providers,
        ICatalogStore store,
        IGitClient git,
        ProjectInspector inspector,
        ILogger<CatalogService> logger)
    {
        _providers = providers.ToList();
        _store = store;
        _git = git;
        _inspector = inspector;
        _logger = logger;
    }

    public CatalogState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public bool IsScanning => Volatile.Read(ref _scanning) != 0;

    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken ct)
    {
        var loaded = await _store.LoadAsync(ct).ConfigureAwait(false);
        lock (_lock)
        {
            _state = loaded;
            _initialized = true;
        }

        OnChanged();
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        bool init;
        lock (_lock)
        {
            init = _initialized;
        }

        if (!init)
        {
            await InitializeAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task ScanAsync(IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _scanning, 1);
        OnChanged();
        try
        {
            var settings = State.Settings;
            var selected = _providers.Where(p => IsEnabled(p.Id, settings)).ToList();
            var results = new ConcurrentDictionary<string, IReadOnlyList<CatalogItem>>(StringComparer.Ordinal);
            var failed = new ConcurrentBag<string>();

            await Task.WhenAll(selected.Select(async provider =>
            {
                try
                {
                    if (!await provider.IsAvailableAsync(ct).ConfigureAwait(false))
                    {
                        _logger.LogInformation("Provider {Provider} not available; skipped", provider.Id);
                        results[provider.Id] = [];
                        return;
                    }

                    progress?.Report(new ScanProgress(provider.Id, null, 0));
                    results[provider.Id] = await provider.DiscoverAsync(settings, progress, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogError(ex, "Provider {Provider} failed; keeping its previous items", provider.Id);
                    failed.Add(provider.Id);
                }
            })).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            // Filesystem items that were not found this time (manually added folders, items outside the roots/depth)
            // stay as long as the folder still exists.
            var previous = State;
            var fsFound = results.TryGetValue(Sources.FileSystem, out var fsItems)
                ? fsItems.Select(i => i.Id).ToHashSet(StringComparer.Ordinal)
                : null;
            var keptFs = new List<CatalogItem>();
            if (fsFound is not null)
            {
                foreach (var old in previous.Items.Where(i => i.Source == Sources.FileSystem && !fsFound.Contains(i.Id) && i.Path is not null))
                {
                    var again = await _inspector.InspectAsync(old.Path!, fetchGit: false, force: true, computeSize: false, ct).ConfigureAwait(false);
                    if (again is not null)
                    {
                        keptFs.Add(again with { SizeBytes = old.SizeBytes });
                    }
                }
            }

            UpdateState(s =>
            {
                var merged = Merge(s, results, failed.ToHashSet(StringComparer.Ordinal), keptFs);
                return s with { Items = merged, LastScanAt = DateTimeOffset.Now };
            });
            await PersistAsync(ct).ConfigureAwait(false);
            progress?.Report(new ScanProgress("done", null, State.Items.Count));
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
            _scanGate.Release();
            OnChanged();
        }
    }

    /// <summary>
    /// Merges provider results into the previous item list: providers that ran replace their items,
    /// providers that failed or were not run keep theirs (unless disabled in settings), ids are unique.
    /// </summary>
    internal static List<CatalogItem> Merge(
        CatalogState previous,
        IReadOnlyDictionary<string, IReadOnlyList<CatalogItem>> results,
        IReadOnlySet<string> failedProviders,
        IReadOnlyList<CatalogItem> keptFileSystemItems)
    {
        var byId = new Dictionary<string, CatalogItem>(StringComparer.Ordinal);
        foreach (var old in previous.Items)
        {
            var ranOk = results.ContainsKey(old.Source) && !failedProviders.Contains(old.Source);
            var disabled = !IsEnabled(old.Source, previous.Settings);
            if (!ranOk && !disabled)
            {
                byId[old.Id] = old;
            }
        }

        foreach (var item in keptFileSystemItems)
        {
            byId[item.Id] = item;
        }

        foreach (var (_, items) in results)
        {
            foreach (var item in items)
            {
                byId[item.Id] = WithMetaActivity(item, previous.Meta);
            }
        }

        return byId.Values.Select(i => WithMetaActivity(i, previous.Meta)).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static CatalogItem WithMetaActivity(CatalogItem item, IReadOnlyDictionary<string, UserMeta> meta)
    {
        if (meta.TryGetValue(item.Id, out var m) && m.LastRunAt is { } run && (item.LastActivity is null || run > item.LastActivity))
        {
            return item with { LastActivity = run };
        }

        return item;
    }

    private static bool IsEnabled(string providerId, TezgahSettings settings)
    {
        if (providerId == Sources.Registry)
        {
            return settings.IncludeInstalledApps;
        }

        if (PackageSources.Contains(providerId))
        {
            return settings.IncludePackageTools;
        }

        return true;
    }

    public async Task RefreshItemAsync(string itemId, bool fetchGit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var existing = State.Items.FirstOrDefault(i => i.Id == itemId);
        if (existing is null)
        {
            return;
        }

        CatalogItem? fresh;
        if (existing.Source == Sources.FileSystem && existing.Path is not null)
        {
            fresh = await _inspector.InspectAsync(existing.Path, fetchGit, force: true, computeSize: true, ct).ConfigureAwait(false);
        }
        else
        {
            var provider = _providers.FirstOrDefault(p => p.Id == existing.Source);
            if (provider is null || !await provider.IsAvailableAsync(ct).ConfigureAwait(false))
            {
                return;
            }

            var all = await provider.DiscoverAsync(State.Settings, null, ct).ConfigureAwait(false);
            fresh = all.FirstOrDefault(i => i.Id == itemId);
        }

        UpdateState(s =>
        {
            var items = s.Items.Where(i => i.Id != itemId).ToList();
            if (fresh is not null)
            {
                items.Add(WithMetaActivity(fresh, s.Meta));
            }

            return s with { Items = items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList() };
        });
        await PersistAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateMetaAsync(string itemId, Func<UserMeta, UserMeta> update, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        ArgumentNullException.ThrowIfNull(update);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        UpdateState(s =>
        {
            var meta = new Dictionary<string, UserMeta>(s.Meta, StringComparer.Ordinal);
            var current = meta.TryGetValue(itemId, out var m) ? m : new UserMeta();
            var updated = update(current);
            meta[itemId] = updated;
            var items = s.Items.Select(i => i.Id == itemId ? WithMetaActivity(i, meta) : i).ToList();
            return s with { Meta = meta, Items = items };
        });
        await PersistAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateSettingsAsync(Func<TezgahSettings, TezgahSettings> update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        UpdateState(s => s with { Settings = update(s.Settings) });
        await PersistAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveItemAsync(string itemId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        UpdateState(s =>
        {
            var meta = new Dictionary<string, UserMeta>(s.Meta, StringComparer.Ordinal);
            meta.Remove(itemId);
            return s with { Items = s.Items.Where(i => i.Id != itemId).ToList(), Meta = meta };
        });
        await PersistAsync(ct).ConfigureAwait(false);
    }

    public async Task<CatalogItem?> AddFolderAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!FsUtil.IsRealDirectory(full))
        {
            return null;
        }

        var item = await _inspector.InspectAsync(full, fetchGit: false, force: true, computeSize: true, ct).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        UpdateState(s =>
        {
            var items = s.Items.Where(i => i.Id != item.Id).ToList();
            items.Add(WithMetaActivity(item, s.Meta));
            return s with { Items = items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList() };
        });
        await PersistAsync(ct).ConfigureAwait(false);
        return item;
    }

    public async Task<CatalogItem?> CloneAsync(string gitUrl, string targetParent, IProgress<string>? progress, CancellationToken ct)
    {
        if (!CloneUrlValidator.IsValid(gitUrl, out var error))
        {
            throw new ArgumentException(error, nameof(gitUrl));
        }

        ArgumentException.ThrowIfNullOrEmpty(targetParent);
        if (!await _git.IsAvailableAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(L.T("git bulunamadı. Lütfen Git for Windows kurun.", "git was not found. Please install Git for Windows."));
        }

        var parent = Path.GetFullPath(targetParent);
        Directory.CreateDirectory(parent);
        var baseName = CloneUrlValidator.FolderNameFor(gitUrl);
        var target = Path.Combine(parent, baseName);
        for (var n = 2; Directory.Exists(target) || File.Exists(target); n++)
        {
            target = Path.Combine(parent, $"{baseName}-{n}");
        }

        var url = gitUrl.Trim();
        progress?.Report($"git clone {url}");
        string[] args =
        [
            "-c", "protocol.ext.allow=never", "-c", "protocol.file.allow=never", "-c", "protocol.fd.allow=never",
            "clone", "--progress", "--", url, target,
        ];
        var result = await Cli.RunAsync("git", args, parent, CloneTimeout, ct, GitClient.GitEnvironment, line => progress?.Report(line)).ConfigureAwait(false);
        if (!result.Success)
        {
            var message = result.TimedOut ? L.T("git clone zaman aşımına uğradı.", "git clone timed out.") : result.StdErr.Trim();
            _logger.LogWarning("git clone failed: {Error}", message);
            throw new InvalidOperationException(message.Length > 0 ? message : L.T("git clone başarısız oldu.", "git clone failed."));
        }

        return await AddFolderAsync(target, ct).ConfigureAwait(false);
    }

    public UserMeta GetMeta(string itemId) =>
        State.Meta.TryGetValue(itemId, out var m) ? m : new UserMeta();

    public IReadOnlyList<RunProfile> GetProfiles(string itemId)
    {
        var state = State;
        var item = state.Items.FirstOrDefault(i => i.Id == itemId);
        var meta = state.Meta.TryGetValue(itemId, out var m) ? m : new UserMeta();
        var detected = item?.RunProfiles ?? [];
        var custom = meta.CustomProfiles.Select(p => p.Origin.Length == 0 ? p with { Origin = "user" } : p);
        var all = detected.Concat(custom).DistinctBy(p => p.Id).ToList();
        if (meta.DefaultProfileId is { } wanted && all.Any(p => p.Id == wanted))
        {
            return all.Select(p => p.IsDefault == (p.Id == wanted) ? p : p with { IsDefault = p.Id == wanted }).ToList();
        }

        // Exactly one default at most.
        var seen = false;
        return all.Select(p =>
        {
            if (!p.IsDefault)
            {
                return p;
            }

            if (seen)
            {
                return p with { IsDefault = false };
            }

            seen = true;
            return p;
        }).ToList();
    }

    public bool IsForgotten(CatalogItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var state = State;
        var meta = state.Meta.TryGetValue(item.Id, out var m) ? m : null;
        if (meta?.Favorite == true)
        {
            return false;
        }

        var last = FsUtil.Max(item.LastActivity, meta?.LastRunAt);
        if (last is null)
        {
            return false;
        }

        return DateTimeOffset.Now - last.Value > TimeSpan.FromDays(Math.Max(1, state.Settings.ForgottenAfterDays));
    }

    private void UpdateState(Func<CatalogState, CatalogState> update)
    {
        lock (_lock)
        {
            _state = update(_state);
        }

        OnChanged();
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(State, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Saving the catalog failed");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Saving the catalog failed");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void Dispose()
    {
        _scanGate.Dispose();
        _saveGate.Dispose();
    }

    private void OnChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "A Changed handler threw");
        }
    }
}
