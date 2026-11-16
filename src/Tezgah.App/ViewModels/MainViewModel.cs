using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.App.Services;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.App.ViewModels;

/// <summary>Everything the main window shows. No WPF types: all UI interaction goes through the service interfaces.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(50);

    private readonly ICatalogService _catalog;
    private readonly IRunService _runs;
    private readonly IUninstallService _uninstall;
    private readonly ICleanupService _cleanup;
    private readonly IGitClient _git;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly IClipboardService _clipboard;
    private readonly IStrings _s;
    private readonly IUiDispatcher _ui;
    private readonly IAppearanceService _appearance;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Dictionary<string, ItemViewModel> _items = new(StringComparer.Ordinal);
    private IDisposable? _flushTimer;
    private CancellationTokenSource? _scanCts;
    private int _syncQueued;
    private int _notificationVersion;
    private bool _initialized;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ItemFilter _activeFilter = ItemFilter.All;

    [ObservableProperty]
    private SortMode _sortMode = SortMode.Activity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isScanning;

    [ObservableProperty]
    private string _scanStatusText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _lastScanText = string.Empty;

    [ObservableProperty]
    private string? _notification;

    [ObservableProperty]
    private bool _notificationIsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoaded;

    [ObservableProperty]
    private bool _isEditingMeta;

    [ObservableProperty]
    private string _editTagsText = string.Empty;

    [ObservableProperty]
    private string _editNoteText = string.Empty;

    [ObservableProperty]
    private int _runningCount;

    public MainViewModel(
        ICatalogService catalog,
        IRunService runs,
        IUninstallService uninstall,
        ICleanupService cleanup,
        IGitClient git,
        IDialogService dialogs,
        IShellService shell,
        IClipboardService clipboard,
        IStrings strings,
        IUiDispatcher ui,
        IAppearanceService appearance,
        AppMode mode,
        TimeProvider? time = null,
        ILogger<MainViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(mode);
        _catalog = catalog;
        _runs = runs;
        _uninstall = uninstall;
        _cleanup = cleanup;
        _git = git;
        _dialogs = dialogs;
        _shell = shell;
        _clipboard = clipboard;
        _s = strings;
        _ui = ui;
        _appearance = appearance;
        _time = time ?? TimeProvider.System;
        _log = logger ?? (ILogger)NullLogger.Instance;
        IsDemo = mode.IsDemo;
    }

    public bool IsDemo { get; }

    /// <summary>Filtered + sorted view of the catalog, updated in place so selection and scroll position survive.</summary>
    public ObservableCollection<ItemViewModel> VisibleItems { get; } = [];

    public IEnumerable<ItemViewModel> AllItems => _items.Values;

    public bool HasSelection => SelectedItem is not null;

    public bool IsEmpty => IsLoaded && !IsScanning && _items.Count == 0;

    public bool HasNoMatches => IsLoaded && _items.Count > 0 && VisibleItems.Count == 0;

    public int CountAll => _items.Values.Count(i => !i.Meta.Hidden);

    public int CountRepos => _items.Values.Count(i => !i.Meta.Hidden && i.IsProject);

    public int CountApps => _items.Values.Count(i => !i.Meta.Hidden && i.Kind == ItemKind.InstalledApp);

    public int CountTools => _items.Values.Count(i => !i.Meta.Hidden && i.Kind == ItemKind.PackageTool);

    public int CountForgotten => _items.Values.Count(i => !i.Meta.Hidden && i.IsForgotten);

    public int CountFavorites => _items.Values.Count(i => !i.Meta.Hidden && i.IsFavorite);

    public bool IsVsCodeAvailable => _shell.IsVsCodeAvailable;

    public string DefaultCloneParent =>
        _catalog.State.Settings.ScanRoots.FirstOrDefault(Directory.Exists)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");

    // ───────────────────────────── lifecycle ─────────────────────────────

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _catalog.Changed += OnCatalogChanged;
        _runs.RunningChanged += OnRunningChanged;
        _flushTimer = _ui.StartTimer(FlushInterval, FlushOutput);
        try
        {
            await _catalog.InitializeAsync(CancellationToken.None);
            var settings = _catalog.State.Settings;
            _appearance.ApplyTheme(settings.Theme);
            _appearance.ApplyLanguage(settings.Language);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Catalog initialization failed");
            Notify(_s.Format("Error_Init", ex.Message), isError: true);
        }

        SyncFromCatalog();
        SyncRuns();
        IsLoaded = true;
        RefreshTexts();
    }

    public void Dispose()
    {
        _catalog.Changed -= OnCatalogChanged;
        _runs.RunningChanged -= OnRunningChanged;
        _flushTimer?.Dispose();
        _scanCts?.Dispose();
        foreach (var run in _items.Values.SelectMany(i => i.Runs))
        {
            run.Dispose();
        }
    }

    /// <summary>Asked by the window on close. False = keep the window open.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        var running = _runs.Running.Count(p => !p.HasExited);
        if (running == 0)
        {
            return true;
        }

        if (!_dialogs.Confirm(_s.Text("Close_Title"), _s.Format("Close_Message", running), _s.Text("Close_StopAndExit"), _s.Text("Common_Cancel")))
        {
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _runs.StopAllAsync(cts.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "StopAll failed on exit");
        }

        return true;
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        // coalesce bursts of Changed into one UI-thread sync
        if (Interlocked.Exchange(ref _syncQueued, 1) == 0)
        {
            _ui.Post(() =>
            {
                Volatile.Write(ref _syncQueued, 0);
                SyncFromCatalog();
            });
        }
    }

    private void OnRunningChanged(object? sender, EventArgs e) => _ui.Post(SyncRuns);

    // ───────────────────────────── catalog → view-models ─────────────────────────────

    internal void SyncFromCatalog()
    {
        var state = _catalog.State;
        var now = _time.GetLocalNow();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in state.Items)
        {
            seen.Add(item.Id);
            var meta = state.Meta.GetValueOrDefault(item.Id) ?? new UserMeta();
            var profiles = _catalog.GetProfiles(item.Id);
            var forgotten = _catalog.IsForgotten(item);
            if (_items.TryGetValue(item.Id, out var vm))
            {
                if (!ReferenceEquals(vm.Item, item) || !ReferenceEquals(vm.Meta, meta) || vm.IsForgotten != forgotten)
                {
                    vm.Update(item, meta, profiles, forgotten, now);
                }
            }
            else
            {
                _items[item.Id] = new ItemViewModel(item, meta, profiles, forgotten, now, _s);
            }
        }

        // An item that left the catalog (renamed folder, rescan, provider turned off) keeps its view-model while any of its
        // runs is alive: otherwise the process would keep running with no console and no Stop button anywhere.
        foreach (var id in _items.Keys.Where(k => !seen.Contains(k) && !HasLiveRun(k)).ToList())
        {
            var gone = _items[id];
            _items.Remove(id);
            foreach (var r in gone.Runs)
            {
                r.Dispose();
            }
        }

        if (SelectedItem is not null && !_items.ContainsKey(SelectedItem.Id))
        {
            SelectedItem = null;
            IsEditingMeta = false;
        }

        IsScanning = _catalog.IsScanning || _scanCts is not null;
        UpdateRunningFlags();
        ApplyFilter();
        UpdateStatus();
        OnPropertyChanged(nameof(IsEmpty));
    }

    internal void SyncRuns()
    {
        foreach (var proc in _runs.Running)
        {
            if (_items.TryGetValue(proc.ItemId, out var item))
            {
                Attach(item, proc);
            }
        }

        UpdateRunningFlags();
        if (ActiveFilter == ItemFilter.Running)
        {
            ApplyFilter();
        }

        UpdateStatus();
    }

    private RunSessionViewModel Attach(ItemViewModel item, IRunningProcess proc)
    {
        var existing = item.Runs.FirstOrDefault(r => r.RunId == proc.RunId);
        if (existing is not null)
        {
            return existing;
        }

        var session = new RunSessionViewModel(proc, _runs, _clipboard, _s);
        item.Runs.Add(session);
        item.SelectedRun ??= session;
        return session;
    }

    private bool HasLiveRun(string itemId) =>
        _runs.ForItem(itemId).Any(p => !p.HasExited) || (_items.TryGetValue(itemId, out var vm) && vm.Runs.Any(r => r.IsRunning && !r.Process.HasExited));

    /// <summary>True while any process started from Tezgah is alive (the same source the close prompt uses).</summary>
    public bool HasLiveRuns => _runs.Running.Any(p => !p.HasExited);

    private void UpdateRunningFlags()
    {
        var running = _runs.Running.Where(p => !p.HasExited).Select(p => p.ItemId).ToHashSet(StringComparer.Ordinal);
        foreach (var vm in _items.Values)
        {
            vm.IsRunning = running.Contains(vm.Id) || vm.Runs.Any(r => r.IsRunning && !r.Process.HasExited);
        }

        RunningCount = _items.Values.Count(i => i.IsRunning);
    }

    private void FlushOutput()
    {
        var anyExited = false;
        foreach (var item in _items.Values)
        {
            foreach (var run in item.Runs)
            {
                var wasRunning = run.IsRunning;
                run.Flush();
                anyExited |= wasRunning && !run.IsRunning;
            }
        }

        if (anyExited)
        {
            UpdateRunningFlags();
            if (_items.Keys.Any(k => !_catalog.State.Items.Any(i => i.Id == k)))
            {
                SyncFromCatalog(); // drop items kept only for their (now finished) runs
            }

            UpdateStatus();
        }
    }

    // ───────────────────────────── filtering ─────────────────────────────

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnActiveFilterChanged(ItemFilter value) => ApplyFilter();

    partial void OnSortModeChanged(SortMode value) => ApplyFilter();

    partial void OnSelectedItemChanged(ItemViewModel? oldValue, ItemViewModel? newValue)
    {
        // Only a real selection change ends a tag/note edit; a null pushed back by the list never discards it.
        if (newValue is not null && !ReferenceEquals(oldValue, newValue))
        {
            IsEditingMeta = false;
        }

        if (oldValue is not null && newValue is not null && !_applyingFilter)
        {
            ApplyFilter(); // the previously pinned item may no longer match
        }
    }

    private bool _applyingFilter;

    internal void ApplyFilter()
    {
        // The selected item stays visible even when it stops matching the search/filter: removing it from the list would
        // make WPF clear the selection, hiding its console and dropping an unsaved tag/note edit.
        var selected = SelectedItem;
        IEnumerable<ItemViewModel> q = _items.Values.Where(i =>
            (ReferenceEquals(i, selected) && !i.Meta.Hidden) || (!i.Meta.Hidden && PassesFilter(i) && i.Matches(SearchText)));
        q = SortMode switch
        {
            SortMode.Name => q.OrderBy(i => i.Name, StringComparer.Create(Formatting.Culture, ignoreCase: true)),
            SortMode.Size => q.OrderByDescending(i => i.Item.SizeBytes ?? -1).ThenBy(i => i.Name, StringComparer.Create(Formatting.Culture, ignoreCase: true)),
            _ => q.OrderByDescending(i => i.IsRunning).ThenByDescending(i => i.EffectiveActivity ?? DateTimeOffset.MinValue),
        };
        _applyingFilter = true;
        try
        {
            Reconcile(VisibleItems, q.ToList());
        }
        finally
        {
            _applyingFilter = false;
        }

        OnPropertyChanged(nameof(HasNoMatches));
    }

    private bool PassesFilter(ItemViewModel i) => ActiveFilter switch
    {
        ItemFilter.Repos => i.IsProject,
        ItemFilter.Apps => i.Kind == ItemKind.InstalledApp,
        ItemFilter.Tools => i.Kind == ItemKind.PackageTool,
        ItemFilter.Running => i.IsRunning,
        ItemFilter.Forgotten => i.IsForgotten,
        ItemFilter.Favorites => i.IsFavorite,
        _ => true,
    };

    /// <summary>Makes <paramref name="target"/> equal to <paramref name="desired"/> with minimal moves/inserts/removes.</summary>
    internal static void Reconcile<T>(ObservableCollection<T> target, IList<T> desired)
        where T : class
    {
        var keep = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i]))
            {
                continue;
            }

            var old = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], desired[i]))
                {
                    old = j;
                    break;
                }
            }

            if (old >= 0)
            {
                target.Move(old, i);
            }
            else
            {
                target.Insert(i, desired[i]);
            }
        }
    }

    // ───────────────────────────── status texts ─────────────────────────────

    private void UpdateStatus()
    {
        var visible = _items.Values.Where(i => !i.Meta.Hidden).ToList();
        var bytes = visible.Sum(i => i.Item.SizeBytes ?? 0);
        StatusText = _s.Format("Status_Totals", CountRepos, CountApps, CountTools, Formatting.Bytes(bytes), CountForgotten, RunningCount);
        var last = _catalog.State.LastScanAt;
        LastScanText = last is null ? _s.Text("Status_NeverScanned") : _s.Format("Status_LastScan", Formatting.Relative(last, _time.GetLocalNow(), _s));
        OnPropertyChanged(nameof(CountAll));
        OnPropertyChanged(nameof(CountRepos));
        OnPropertyChanged(nameof(CountApps));
        OnPropertyChanged(nameof(CountTools));
        OnPropertyChanged(nameof(CountForgotten));
        OnPropertyChanged(nameof(CountFavorites));
    }

    /// <summary>Re-evaluates every localized text (after a language switch).</summary>
    public void RefreshTexts()
    {
        foreach (var vm in _items.Values)
        {
            vm.RefreshTexts();
            foreach (var r in vm.Runs)
            {
                r.UpdateStatus();
            }
        }

        UpdateStatus();
    }

    private void Notify(string message, bool isError = false)
    {
        Notification = message;
        NotificationIsError = isError;
        var version = Interlocked.Increment(ref _notificationVersion);
        _ = ClearNotificationLaterAsync(version);
    }

    private async Task ClearNotificationLaterAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), _time);
        _ui.Post(() =>
        {
            if (version == _notificationVersion)
            {
                Notification = null;
            }
        });
    }

    [RelayCommand]
    private void DismissNotification() => Notification = null;

    // ───────────────────────────── scan / add / clone / settings ─────────────────────────────

    /// <summary>Starts a scan; invoked again while scanning it cancels.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ScanAsync()
    {
        if (_scanCts is not null)
        {
            await _scanCts.CancelAsync();
            return;
        }

        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        ScanStatusText = _s.Text("Scan_Starting");
        var progress = new SyncProgressPoster<ScanProgress>(_ui, p =>
        {
            if (_scanCts is not null)
            {
                ScanStatusText = _s.Format("Scan_Progress", ShortenPath(p.CurrentPath ?? p.Stage, 64), p.ItemsFound);
            }
        });
        try
        {
            await _catalog.ScanAsync(progress, _scanCts.Token);
            Notify(_s.Format("Scan_Done", _catalog.State.Items.Count));
        }
        catch (OperationCanceledException)
        {
            Notify(_s.Text("Scan_Cancelled"));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scan failed");
            Notify(_s.Format("Scan_Failed", ex.Message), isError: true);
        }
        finally
        {
            _scanCts.Dispose();
            _scanCts = null;
            IsScanning = false;
            ScanStatusText = string.Empty;
            SyncFromCatalog();
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var folder = _dialogs.PickFolder(_s.Text("Add_PickFolder"), null);
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        try
        {
            var item = await _catalog.AddFolderAsync(folder, CancellationToken.None);
            SyncFromCatalog();
            if (item is null)
            {
                Notify(_s.Text("Add_NotRecognized"), isError: true);
                return;
            }

            Select(item.Id);
        }
        catch (Exception ex)
        {
            Notify(_s.Format("Add_Failed", ex.Message), isError: true);
        }
    }

    [RelayCommand]
    private void Clone()
    {
        var vm = new CloneViewModel(_catalog, _dialogs, _s, DefaultCloneParent);
        _dialogs.ShowClone(vm);
        if (vm.Result is { } item)
        {
            SyncFromCatalog();
            Select(item.Id);
            Notify(_s.Format("Clone_Done", item.Name));
        }
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var current = _catalog.State.Settings;
        var vm = new SettingsViewModel(current, _dialogs, _s);
        _dialogs.ShowSettings(vm);
        if (!vm.Saved)
        {
            return;
        }

        var updated = vm.ToSettings(current);
        try
        {
            await _catalog.UpdateSettingsAsync(_ => updated, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Notify(_s.Format("Settings_SaveFailed", ex.Message), isError: true);
            return;
        }

        _appearance.ApplyTheme(updated.Theme);
        if (updated.Language != current.Language)
        {
            _appearance.ApplyLanguage(updated.Language);
            RefreshTexts();
        }

        SyncFromCatalog();
        if (vm.ScanSettingsChanged(updated) && !IsScanning
            && _dialogs.Confirm(_s.Text("Settings_Title"), _s.Text("Settings_RescanPrompt"), _s.Text("Scan_Button"), _s.Text("Common_Later")))
        {
            await ScanAsync();
        }
    }

    // ───────────────────────────── run / stop ─────────────────────────────

    [RelayCommand]
    private Task RunAsync()
    {
        var item = SelectedItem;
        return item?.DefaultProfile is { } p ? StartAsync(item, p.Profile) : Task.CompletedTask;
    }

    [RelayCommand]
    private Task RunProfileAsync(ProfileViewModel? profile)
    {
        var item = SelectedItem;
        return item is not null && profile is not null ? StartAsync(item, profile.Profile) : Task.CompletedTask;
    }

    internal async Task<RunSessionViewModel?> StartAsync(ItemViewModel item, RunProfile profile)
    {
        if (!item.IsTrusted && !await AskTrustAsync(item, profile))
        {
            return null;
        }

        item.IsBusy = true;
        try
        {
            IRunningProcess proc;
            try
            {
                proc = await _runs.StartAsync(item.Id, profile, CancellationToken.None);
            }
            catch (UntrustedItemException)
            {
                // trust was revoked elsewhere or the service has a stricter view: ask once more, then retry once
                if (!await AskTrustAsync(item, profile))
                {
                    return null;
                }

                proc = await _runs.StartAsync(item.Id, profile, CancellationToken.None);
            }

            var session = Attach(item, proc);
            item.SelectedRun = session;
            UpdateRunningFlags();
            UpdateStatus();
            return session;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Start failed for {Item} {Profile}", item.Id, profile.Id);
            Notify(_s.Format("Run_Failed", profile.Label, ex.Message), isError: true);
            return null;
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private Task<bool> AskTrustAsync(ItemViewModel item, RunProfile profile) => AskTrustAsync(item, profile.Command.Display);

    /// <summary>
    /// Workspace-trust prompt for anything that executes the folder's code or honours its repository config
    /// (runs, but also git pull/fetch, which run hooks, filters and core.sshCommand).
    /// </summary>
    private async Task<bool> AskTrustAsync(ItemViewModel item, string commandLine)
    {
        var meta = _catalog.GetMeta(item.Id);
        var claimed = item.Git?.RemoteUrl ?? item.Item.WebUrl;
        var verified = meta.ClonedFrom is { } cloned && (claimed is null || string.Equals(cloned, claimed, StringComparison.OrdinalIgnoreCase));
        var prompt = new TrustPrompt(item.Name, item.Path, verified ? meta.ClonedFrom : claimed, commandLine, verified);
        if (!_dialogs.ConfirmTrust(prompt))
        {
            return false;
        }

        await _catalog.UpdateMetaAsync(item.Id, m => m with { Trusted = true }, CancellationToken.None);
        SyncFromCatalog();
        return true;
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        var item = SelectedItem;
        if (item is null)
        {
            return;
        }

        try
        {
            if (item.SelectedRun is { IsRunning: true } run)
            {
                await _runs.StopAsync(run.RunId, CancellationToken.None);
            }
            else
            {
                foreach (var p in _runs.ForItem(item.Id).Where(p => !p.HasExited))
                {
                    await _runs.StopAsync(p.RunId, CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            Notify(_s.Format("Run_StopFailed", ex.Message), isError: true);
        }
    }

    [RelayCommand]
    private async Task CloseRunAsync(RunSessionViewModel? session)
    {
        var item = SelectedItem;
        if (session is null || item is null)
        {
            return;
        }

        if (session.IsRunning)
        {
            try
            {
                await _runs.StopAsync(session.RunId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Stop on tab close failed");
            }
        }

        var index = item.Runs.IndexOf(session);
        item.Runs.Remove(session);
        session.Dispose();
        if (ReferenceEquals(item.SelectedRun, session) || item.SelectedRun is null)
        {
            item.SelectedRun = item.Runs.Count == 0 ? null : item.Runs[Math.Clamp(index, 0, item.Runs.Count - 1)];
        }

        UpdateRunningFlags();
    }

    [RelayCommand]
    private void OpenUrl(Uri? url)
    {
        if (url is not null && !_shell.OpenUrl(url))
        {
            Notify(_s.Text("Open_Failed"), isError: true);
        }
    }

    // ───────────────────────────── item actions ─────────────────────────────

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedItem?.Path is { } p && !_shell.OpenFolder(p))
        {
            Notify(_s.Text("Open_FolderMissing"), isError: true);
        }
    }

    [RelayCommand]
    private void OpenTerminal()
    {
        if (SelectedItem?.Path is { } p && !_shell.OpenTerminal(p))
        {
            Notify(_s.Text("Open_TerminalFailed"), isError: true);
        }
    }

    [RelayCommand]
    private void OpenInVsCode()
    {
        if (SelectedItem?.Path is { } p && !_shell.OpenInVsCode(p))
        {
            Notify(_s.Text("Open_VsCodeMissing"), isError: true);
        }
    }

    [RelayCommand]
    private void OpenLink()
    {
        if (SelectedItem?.LinkUri is { } u)
        {
            OpenUrl(u);
        }
    }

    [RelayCommand]
    private void CopyPath()
    {
        if (SelectedItem?.Path is { } p)
        {
            _clipboard.SetText(p);
            Notify(_s.Text("Copied"));
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(ItemViewModel? target)
    {
        var item = target ?? SelectedItem;
        if (item is null)
        {
            return;
        }

        await _catalog.UpdateMetaAsync(item.Id, m => m with { Favorite = !m.Favorite }, CancellationToken.None);
        SyncFromCatalog();
    }

    [RelayCommand]
    private void BeginEditMeta()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        EditTagsText = string.Join(", ", item.Tags);
        EditNoteText = item.Meta.Note ?? string.Empty;
        IsEditingMeta = true;
    }

    [RelayCommand]
    private async Task SaveMetaAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var tags = EditTagsText.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Create(Formatting.Culture, ignoreCase: true)).ToArray();
        var note = string.IsNullOrWhiteSpace(EditNoteText) ? null : EditNoteText.Trim();
        await _catalog.UpdateMetaAsync(item.Id, m => m with { Tags = tags, Note = note }, CancellationToken.None);
        IsEditingMeta = false;
        SyncFromCatalog();
    }

    [RelayCommand]
    private void CancelEditMeta() => IsEditingMeta = false;

    [RelayCommand]
    private async Task PullAsync()
    {
        if (SelectedItem is not { Path: { } path } item || !item.HasGit)
        {
            return;
        }

        // git pull runs the repository's hooks, filters and transport settings: same trust gate as running its code.
        if (!item.IsTrusted && !await AskTrustAsync(item, "git pull --ff-only"))
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            var result = await _git.PullAsync(path, CancellationToken.None);
            if (result.Success)
            {
                var first = FirstLine(result.StdOut);
                Notify(_s.Format("Git_PullDone", item.Name, first));
            }
            else
            {
                Notify(_s.Format("Git_PullFailed", FirstLine(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr)), isError: true);
            }

            await _catalog.RefreshItemAsync(item.Id, fetchGit: false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Notify(_s.Format("Git_PullFailed", ex.Message), isError: true);
        }
        finally
        {
            item.IsBusy = false;
            SyncFromCatalog();
        }
    }

    [RelayCommand]
    private async Task RefreshItemAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            // Untrusted repositories are refreshed locally only: git fetch honours repository-controlled commands.
            await _catalog.RefreshItemAsync(item.Id, fetchGit: item.HasGit && item.IsTrusted, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Notify(_s.Format("Refresh_Failed", ex.Message), isError: true);
        }
        finally
        {
            item.IsBusy = false;
            SyncFromCatalog();
        }
    }

    [RelayCommand]
    private async Task CleanupAsync()
    {
        if (SelectedItem is not { HasArtifacts: true } item)
        {
            return;
        }

        var vm = new CleanupViewModel(item.Item, _cleanup, _s);
        _dialogs.ShowCleanup(vm);
        if (vm.FreedBytes > 0)
        {
            Notify(_s.Format("Cleanup_Done", Formatting.Bytes(vm.FreedBytes)));
            try
            {
                await _catalog.RefreshItemAsync(item.Id, fetchGit: false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Refresh after cleanup failed");
            }

            SyncFromCatalog();
        }
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var vm = new UninstallViewModel(item.Id, item.Name, _uninstall, _s);
        _dialogs.ShowUninstall(vm);
        if (!vm.Succeeded)
        {
            return;
        }

        // The service is expected to drop the catalog entry (RemoveCatalogEntry step); make sure it is gone.
        if (_catalog.State.Items.Any(i => i.Id == item.Id)
            && vm.Steps.Any(s => s.Kind == UninstallStepKind.RemoveCatalogEntry && s.IsSelected))
        {
            await _catalog.RemoveItemAsync(item.Id, CancellationToken.None);
        }

        SyncFromCatalog();
        Notify(_s.Format("Uninstall_Removed", item.Name));
    }

    // ───────────────────────────── helpers ─────────────────────────────

    public void Select(string itemId)
    {
        if (!_items.TryGetValue(itemId, out var vm))
        {
            return;
        }

        if (!VisibleItems.Contains(vm))
        {
            SearchText = string.Empty;
            ActiveFilter = ItemFilter.All;
        }

        SelectedItem = vm;
    }

    private static string FirstLine(string? text) =>
        (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    internal static string ShortenPath(string path, int max)
    {
        if (path.Length <= max)
        {
            return path;
        }

        var head = max / 3;
        var tail = max - head - 1;
        return path[..head] + "…" + path[^tail..];
    }
}

/// <summary>Progress that posts to the UI thread via <see cref="IUiDispatcher"/> (testable, no SynchronizationContext needed).</summary>
public sealed class SyncProgressPoster<T>(IUiDispatcher ui, Action<T> handler) : IProgress<T>
{
    public void Report(T value) => ui.Post(() => handler(value));
}
