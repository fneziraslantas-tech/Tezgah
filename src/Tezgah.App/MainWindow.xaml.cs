using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Tezgah.App.Services;
using Tezgah.App.ViewModels;

namespace Tezgah.App;

/// <summary>Main window. Only view concerns live here (focus, menus, close confirmation); logic is in <see cref="MainViewModel"/>.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly IStrings _strings;
    private bool _closeConfirmed;

    public MainWindow(MainViewModel viewModel, IStrings strings)
    {
        _vm = viewModel;
        _strings = strings;
        DataContext = viewModel;
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
        {
            if (SearchBox.Text.Length > 0)
            {
                _vm.SearchText = string.Empty;
            }
            else
            {
                ItemsList.Focus();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin && ItemsList.Items.Count > 0)
        {
            // arrow down from the search box jumps into the list
            if (ItemsList.SelectedIndex < 0)
            {
                ItemsList.SelectedIndex = 0;
            }

            (ItemsList.ItemContainerGenerator.ContainerFromIndex(ItemsList.SelectedIndex) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e) => OpenMenu((Button)sender, ((Button)sender).ContextMenu);

    private void ProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        var item = _vm.SelectedItem;
        if (item is null)
        {
            return;
        }

        var menu = new ContextMenu();
        var first = true;
        foreach (var group in item.ProfileGroups)
        {
            if (!first)
            {
                menu.Items.Add(new Separator());
            }

            first = false;
            menu.Items.Add(new MenuItem
            {
                Header = new TextBlock { Text = group.Title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Opacity = 0.7 },
                IsEnabled = false,
            });
            foreach (var p in group.Profiles)
            {
                var header = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
                var title = new TextBlock { Text = p.Label, FontWeight = p.IsDefault ? FontWeights.SemiBold : FontWeights.Normal };
                if (p.IsDefault)
                {
                    title.Text += "  · " + _strings.Text("Run_Default");
                }

                header.Children.Add(title);
                header.Children.Add(new TextBlock
                {
                    Text = "$ " + p.CommandLine,
                    FontFamily = (FontFamily)FindResource("MonoFont"),
                    FontSize = 11.5,
                    Opacity = 0.75,
                    MaxWidth = 640,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                menu.Items.Add(new MenuItem
                {
                    Header = header,
                    Command = _vm.RunProfileCommand,
                    CommandParameter = p,
                    Icon = new TextBlock { Text = Views.IconGlyphConverter.Glyph(p.Kind), Style = (Style)FindResource("Icon") },
                    ToolTip = p.CommandLine,
                });
            }
        }

        OpenMenu((Button)sender, menu);
    }

    private static void OpenMenu(FrameworkElement target, ContextMenu? menu)
    {
        if (menu is null)
        {
            return;
        }

        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.DataContext = target.DataContext;
        menu.IsOpen = true;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (_closeConfirmed)
        {
            return;
        }

        if (!_vm.HasLiveRuns)
        {
            _closeConfirmed = true;
            return;
        }

        e.Cancel = true;
        if (await _vm.ConfirmCloseAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Skips the "stop running processes?" prompt (used when the app shuts down programmatically).</summary>
    public void AllowCloseWithoutPrompt() => _closeConfirmed = true;

    /// <summary>Brings the window to the front (second instance launched).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}
