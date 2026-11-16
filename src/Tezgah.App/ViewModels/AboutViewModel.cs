using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tezgah.App.Services;

namespace Tezgah.App.ViewModels;

/// <summary>"About Tezgah": what the app does, how to use it, the safety model, shortcuts and links.</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    /// <summary>Project home (source, issues, releases, private vulnerability reporting).</summary>
    public static readonly Uri RepositoryUrl = new("https://github.com/fneziraslantas-tech/Tezgah");

    public static readonly Uri LicenseUrl = new("https://github.com/fneziraslantas-tech/Tezgah/blob/main/LICENSE");

    private readonly IShellService _shell;
    private readonly IStrings _s;

    [ObservableProperty]
    private string? _error;

    public AboutViewModel(IShellService shell, IStrings strings, string logDirectory, string? version = null)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentException.ThrowIfNullOrEmpty(logDirectory);
        _shell = shell;
        _s = strings;
        LogDirectory = logDirectory;
        Version = version ?? AppVersion(typeof(AboutViewModel).Assembly);
        Shortcuts =
        [
            new("F5", strings.Text("About_Key_Scan")),
            new("Ctrl+F", strings.Text("About_Key_Search")),
            new("Enter", strings.Text("About_Key_Run")),
            new("Ctrl+.", strings.Text("About_Key_Stop")),
            new("Del", strings.Text("About_Key_Remove")),
            new("Ctrl+O", strings.Text("About_Key_Folder")),
            new("Ctrl+T", strings.Text("About_Key_Terminal")),
            new("Ctrl+,", strings.Text("About_Key_Settings")),
            new("F1", strings.Text("About_Key_About")),
        ];
    }

    /// <summary>Product version without build metadata ("0.1.0", not "0.1.0+3f2a...").</summary>
    public string Version { get; }

    public string VersionText => _s.Format("About_Version", Version);

    public string LogDirectory { get; }

    /// <summary>Keyboard shortcuts shown in the dialog (same bindings as MainWindow.xaml).</summary>
    public IReadOnlyList<ShortcutRow> Shortcuts { get; }

    public event EventHandler? CloseRequested;

    internal static string AppVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational) ? assembly.GetName().Version?.ToString(3) : informational;
        if (string.IsNullOrEmpty(version))
        {
            return "?";
        }

        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? version[..plus] : version;
    }

    [RelayCommand]
    private void OpenRepository() => Open(RepositoryUrl);

    [RelayCommand]
    private void OpenLicense() => Open(LicenseUrl);

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = _s.Text("Open_FolderMissing");
            return;
        }

        Error = _shell.OpenFolder(LogDirectory) ? null : _s.Text("Open_FolderMissing");
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Open(Uri url) => Error = _shell.OpenUrl(url) ? null : _s.Text("Open_Failed");
}

/// <summary>One row of the shortcut table: the key chord and what it does.</summary>
public sealed record ShortcutRow(string Keys, string Action);
