using System.Windows;
using Microsoft.Win32;
using Tezgah.App.ViewModels;
using Tezgah.App.Views;

namespace Tezgah.App.Services;

/// <summary>WPF implementation of <see cref="IDialogService"/>: every dialog is modal and owned by the active window.</summary>
public sealed class DialogService(IStrings strings) : IDialogService
{
    private static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
        ?? (Application.Current?.MainWindow is { IsVisible: true } main ? main : null);

    public bool Confirm(string title, string message, string confirmText, string cancelText, bool danger = false) =>
        Show(new ConfirmDialog(title, message, confirmText, cancelText, danger ? ConfirmDialog.Kind.Danger : ConfirmDialog.Kind.Question)) == true;

    public void ShowInfo(string title, string message) =>
        Show(new ConfirmDialog(title, message, strings.Text("Common_Ok"), null, ConfirmDialog.Kind.Info));

    public void ShowError(string title, string message) =>
        Show(new ConfirmDialog(title, message, strings.Text("Common_Ok"), null, ConfirmDialog.Kind.Error));

    public bool ConfirmTrust(TrustPrompt prompt) => Show(new TrustDialog(prompt, strings)) == true;

    public void ShowUninstall(UninstallViewModel viewModel) => Show(new UninstallDialog(viewModel));

    public void ShowCleanup(CleanupViewModel viewModel) => Show(new CleanupDialog(viewModel));

    public void ShowSettings(SettingsViewModel viewModel) => Show(new SettingsDialog(viewModel));

    public void ShowClone(CloneViewModel viewModel) => Show(new CloneDialog(viewModel));

    public string? PickFolder(string title, string? initialDirectory)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrEmpty(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
        {
            dlg.InitialDirectory = initialDirectory;
        }

        var owner = Owner;
        var ok = owner is null ? dlg.ShowDialog() : dlg.ShowDialog(owner);
        return ok == true ? dlg.FolderName : null;
    }

    private static bool? Show(Window dialog)
    {
        var owner = Owner;
        if (owner is not null && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog();
    }
}
