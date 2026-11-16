using Tezgah.App.ViewModels;

namespace Tezgah.App.Services;

/// <summary>Localized strings looked up from the active resource dictionary (Strings.tr.xaml / Strings.en.xaml).</summary>
public interface IStrings
{
    /// <summary>Returns the string for <paramref name="key"/>, or the key itself when missing.</summary>
    string Text(string key);

    /// <summary>Composite-formats the string for <paramref name="key"/> with the current culture.</summary>
    string Format(string key, params object?[] args);
}

/// <summary>UI-thread marshalling without exposing WPF types to view-models.</summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    void Post(Action action);

    /// <summary>Starts a repeating UI-thread timer. Dispose to stop it.</summary>
    IDisposable StartTimer(TimeSpan interval, Action tick);
}

/// <summary>
/// What the trust dialog shows before a folder's code is executed for the first time. <paramref name="RemoteVerified"/> is
/// true only when Tezgah cloned the folder from <paramref name="Remote"/> itself; otherwise the remote is whatever the
/// folder's own <c>.git/config</c> claims and is shown as unverified.
/// </summary>
public sealed record TrustPrompt(string ItemName, string? Path, string? Remote, string CommandLine, bool RemoteVerified = false);

/// <summary>All modal UI. View-models never show windows or message boxes directly.</summary>
public interface IDialogService
{
    bool Confirm(string title, string message, string confirmText, string cancelText, bool danger = false);

    void ShowInfo(string title, string message);

    void ShowError(string title, string message);

    bool ConfirmTrust(TrustPrompt prompt);

    void ShowUninstall(UninstallViewModel viewModel);

    void ShowCleanup(CleanupViewModel viewModel);

    void ShowSettings(SettingsViewModel viewModel);

    void ShowClone(CloneViewModel viewModel);

    string? PickFolder(string title, string? initialDirectory);
}

public interface IClipboardService
{
    void SetText(string text);
}

/// <summary>Applies theme and language to the running application.</summary>
public interface IAppearanceService
{
    /// <summary>"system", "light" or "dark".</summary>
    void ApplyTheme(string theme);

    /// <summary>"tr" or "en". Switches string dictionaries live.</summary>
    void ApplyLanguage(string language);
}

/// <summary>True when the demo fakes are active (shown as a badge so nobody mistakes sample data for real).</summary>
public sealed record AppMode(bool IsDemo);
