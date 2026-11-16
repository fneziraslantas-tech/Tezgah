using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tezgah.App.Services;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.App.ViewModels;

/// <summary>"Free space": lists regenerable artifact folders with checkboxes.</summary>
public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly ICleanupService _service;
    private readonly IStrings _s;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    private bool _isDone;

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    private long _freedBytes;

    public CleanupViewModel(CatalogItem item, ICleanupService service, IStrings strings, bool permanentDelete = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        PermanentDelete = permanentDelete;
        ItemId = item.Id;
        ItemName = item.Name;
        _service = service;
        _s = strings;
        foreach (var a in item.Artifacts.OrderByDescending(a => a.SizeBytes))
        {
            var vm = new ArtifactViewModel(a, item.Path);
            vm.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(TotalText));
                CleanCommand.NotifyCanExecuteChanged();
            };
            Artifacts.Add(vm);
        }
    }

    public string ItemId { get; }

    public string ItemName { get; }

    /// <summary>True when the Recycle Bin is turned off in settings: the selected folders are deleted for good.</summary>
    public bool PermanentDelete { get; }

    public string Title => _s.Format("Cleanup_Title", ItemName);

    public ObservableCollection<ArtifactViewModel> Artifacts { get; } = [];

    public string TotalText => _s.Format("Cleanup_Total", Formatting.Bytes(Artifacts.Where(a => a.IsSelected).Sum(a => a.Artifact.SizeBytes)));

    public event EventHandler? CloseRequested;

    private bool CanClean() => !IsBusy && !IsDone && Artifacts.Any(a => a.IsSelected);

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        IsBusy = true;
        try
        {
            var selected = Artifacts.Where(a => a.IsSelected).Select(a => a.Artifact).ToList();
            FreedBytes = await _service.CleanArtifactsAsync(ItemId, selected, CancellationToken.None);
            ResultText = _s.Format("Cleanup_Done", Formatting.Bytes(FreedBytes));
        }
        catch (Exception ex)
        {
            ResultText = _s.Format("Cleanup_Failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsDone = true;
        }
    }

    [RelayCommand]
    private void Close()
    {
        if (!IsBusy)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed partial class ArtifactViewModel(ArtifactDir artifact, string? root) : ObservableObject
{
    // build/dist/out may hold hand-made files: never pre-selected.
    [ObservableProperty]
    private bool _isSelected = !(artifact ?? throw new ArgumentNullException(nameof(artifact))).Ambiguous;

    public ArtifactDir Artifact { get; } = artifact;

    public bool IsAmbiguous => Artifact.Ambiguous;

    public string Kind => Artifact.Kind;

    public string SizeText => Formatting.Bytes(Artifact.SizeBytes);

    /// <summary>Path relative to the item root when possible.</summary>
    public string DisplayPath => root is not null && Artifact.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        ? "." + Artifact.Path[root.Length..]
        : Artifact.Path;
}

public sealed record ChoiceOption(string Value, string Label);

/// <summary>Edits a copy of <see cref="TezgahSettings"/>; the caller persists it when <see cref="Saved"/> is true.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IStrings _s;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveRootCommand))]
    private string? _selectedRoot;

    [ObservableProperty]
    private string _excludedText;

    [ObservableProperty]
    private int _maxDepth;

    [ObservableProperty]
    private int _forgottenAfterDays;

    [ObservableProperty]
    private bool _includeInstalledApps;

    [ObservableProperty]
    private bool _includePackageTools;

    [ObservableProperty]
    private bool _useRecycleBin;

    [ObservableProperty]
    private string _language;

    [ObservableProperty]
    private string _theme;

    public SettingsViewModel(TezgahSettings settings, IDialogService dialogs, IStrings strings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Original = settings;
        _dialogs = dialogs;
        _s = strings;
        ScanRoots = [.. settings.ScanRoots];
        _excludedText = string.Join(Environment.NewLine, settings.ExcludedDirectoryNames);
        _maxDepth = settings.MaxDepth;
        _forgottenAfterDays = settings.ForgottenAfterDays;
        _includeInstalledApps = settings.IncludeInstalledApps;
        _includePackageTools = settings.IncludePackageTools;
        _useRecycleBin = settings.UseRecycleBin;
        _language = settings.Language == "en" ? "en" : "tr";
        _theme = settings.Theme is "light" or "dark" ? settings.Theme : "system";
        LanguageOptions = [new("tr", _s.Text("Lang_tr")), new("en", _s.Text("Lang_en"))];
        ThemeOptions = [new("system", _s.Text("Theme_System")), new("light", _s.Text("Theme_Light")), new("dark", _s.Text("Theme_Dark"))];
    }

    public TezgahSettings Original { get; }

    public ObservableCollection<string> ScanRoots { get; }

    public IReadOnlyList<ChoiceOption> LanguageOptions { get; }

    public IReadOnlyList<ChoiceOption> ThemeOptions { get; }

    public bool Saved { get; private set; }

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void AddRoot()
    {
        var folder = _dialogs.PickFolder(_s.Text("Settings_PickRoot"), ScanRoots.LastOrDefault());
        if (!string.IsNullOrEmpty(folder) && !ScanRoots.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            ScanRoots.Add(folder);
        }
    }

    private bool CanRemoveRoot() => SelectedRoot is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveRoot))]
    private void RemoveRoot()
    {
        if (SelectedRoot is { } r)
        {
            ScanRoots.Remove(r);
        }
    }

    [RelayCommand]
    private void Save()
    {
        Saved = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public TezgahSettings ToSettings(TezgahSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            ScanRoots = [.. ScanRoots],
            ExcludedDirectoryNames = [.. ExcludedText.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)],
            MaxDepth = Math.Clamp(MaxDepth, 1, 32),
            ForgottenAfterDays = Math.Clamp(ForgottenAfterDays, 1, 3650),
            IncludeInstalledApps = IncludeInstalledApps,
            IncludePackageTools = IncludePackageTools,
            UseRecycleBin = UseRecycleBin,
            Language = Language,
            Theme = Theme,
        };
    }

    public bool ScanSettingsChanged(TezgahSettings updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        return !Original.ScanRoots.SequenceEqual(updated.ScanRoots, StringComparer.OrdinalIgnoreCase)
            || !Original.ExcludedDirectoryNames.SequenceEqual(updated.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase)
            || Original.MaxDepth != updated.MaxDepth
            || Original.IncludeInstalledApps != updated.IncludeInstalledApps
            || Original.IncludePackageTools != updated.IncludePackageTools;
    }
}

/// <summary>"Clone from GitHub": URL + target folder, streams git progress.</summary>
public sealed partial class CloneViewModel : ObservableObject, IDisposable
{
    private readonly ICatalogService _catalog;
    private readonly IDialogService _dialogs;
    private readonly IStrings _s;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    [NotifyPropertyChangedFor(nameof(TargetPreview))]
    private string _url = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    [NotifyPropertyChangedFor(nameof(TargetPreview))]
    private string _targetParent;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    public CloneViewModel(ICatalogService catalog, IDialogService dialogs, IStrings strings, string defaultParent)
    {
        _catalog = catalog;
        _dialogs = dialogs;
        _s = strings;
        _targetParent = defaultParent;
    }

    public ObservableCollection<string> Log { get; } = [];

    public CatalogItem? Result { get; private set; }

    public string TargetPreview => RepoName(Url) is { } name && !string.IsNullOrWhiteSpace(TargetParent)
        ? Path.Combine(TargetParent, name)
        : string.Empty;

    public event EventHandler? CloseRequested;

    /// <summary>Accepts https://, http://, ssh:// and scp-like git@host:path URLs; rejects anything option-like or with spaces.</summary>
    public static bool IsValidGitUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var u = url.Trim();
        if (u.StartsWith('-') || u.Any(char.IsWhiteSpace) || u.Contains("::", StringComparison.Ordinal))
        {
            return false;
        }

        if (u.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colon = u.IndexOf(':', StringComparison.Ordinal);
            return colon > 4 && colon < u.Length - 1;
        }

        return Uri.TryCreate(u, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == "ssh")
            && uri.AbsolutePath.Trim('/').Length > 0;
    }

    public static string? RepoName(string? url)
    {
        if (!IsValidGitUrl(url))
        {
            return null;
        }

        var name = url!.Trim().TrimEnd('/').Split('/', ':').Last();
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." ? null : name;
    }

    private bool CanClone() => !IsBusy && RepoName(Url) is not null && Path.IsPathFullyQualified(TargetParent ?? string.Empty);

    [RelayCommand]
    private void Browse()
    {
        var folder = _dialogs.PickFolder(_s.Text("Clone_PickParent"), TargetParent);
        if (!string.IsNullOrEmpty(folder))
        {
            TargetParent = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task CloneAsync()
    {
        IsBusy = true;
        Error = null;
        Log.Clear();
        _cts = new CancellationTokenSource();
        var succeeded = false;
        try
        {
            var progress = new Progress<string>(line => Log.Add(line));
            var cloned = await _catalog.CloneAsync(Url.Trim(), TargetParent, progress, _cts.Token);
            if (cloned is not null)
            {
                Result = cloned; // only a success sets it: a later failed attempt never clears an earlier result
                succeeded = true;
            }
            else
            {
                Error = _s.Text("Clone_Failed");
            }
        }
        catch (OperationCanceledException)
        {
            Error = _s.Text("Clone_Cancelled");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }

        // Raised only after IsBusy is false: the dialog refuses to close (and cancels) while a clone is running.
        if (succeeded)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _cts = null;
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy)
        {
            _cts?.Cancel();
        }
        else
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
