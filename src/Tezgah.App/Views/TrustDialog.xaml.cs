using System.Windows;
using Tezgah.App.Services;

namespace Tezgah.App.Views;

/// <summary>Workspace-trust prompt shown before a folder's code runs for the first time. Safe default: "Cancel".</summary>
public partial class TrustDialog : Window
{
    public TrustDialog(TrustPrompt prompt, IStrings strings)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(strings);
        InitializeComponent();
        NameText.Text = prompt.ItemName;
        PathText.Text = prompt.Path ?? "—";
        RemoteText.Text = prompt.Remote is null
            ? strings.Text("Trust_UnknownSource")
            : prompt.Remote + "  " + strings.Text(prompt.RemoteVerified ? "Trust_SourceCloned" : "Trust_SourceUnverified");
        CommandText.Text = prompt.CommandLine;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
