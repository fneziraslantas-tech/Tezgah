using System.Windows;

namespace Tezgah.App.Views;

public partial class ConfirmDialog : Window
{
    public enum Kind
    {
        Question,
        Info,
        Error,
        Danger,
    }

    public ConfirmDialog(string title, string message, string confirmText, string? cancelText, Kind kind)
    {
        InitializeComponent();
        TitleText.Text = title;
        Title = title;
        MessageText.Text = message;
        OkButton.Content = confirmText;
        if (cancelText is null)
        {
            CancelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            CancelButton.Content = cancelText;
        }

        (IconText.Text, var brushKey) = kind switch
        {
            Kind.Error => ("", "SystemFillColorCriticalBrush"),
            Kind.Danger => ("", "SystemFillColorCriticalBrush"),
            Kind.Info => ("", "AccentTextFillColorPrimaryBrush"),
            _ => ("", "AccentTextFillColorPrimaryBrush"),
        };
        IconText.SetResourceReference(ForegroundProperty, brushKey);
        if (kind == Kind.Danger)
        {
            OkButton.Style = (Style)FindResource("DangerButton");
            // dangerous confirmations default to the safe button
            OkButton.IsDefault = false;
            CancelButton.IsDefault = true;
            Loaded += (_, _) => CancelButton.Focus();
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
