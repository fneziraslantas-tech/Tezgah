using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tezgah.App.ViewModels;

namespace Tezgah.App.Views;

/// <summary>
/// Attached to the console ListBox: scrolls to the newest line after each batch when the session's AutoScroll is on,
/// and copies selected lines with Ctrl+C. Rebinds when the TabControl reuses the ListBox for another session.
/// </summary>
public static class ConsoleAutoScroll
{
    public static readonly DependencyProperty SessionProperty = DependencyProperty.RegisterAttached(
        "Session", typeof(RunSessionViewModel), typeof(ConsoleAutoScroll), new PropertyMetadata(null, OnSessionChanged));

    private static readonly DependencyProperty HandlerProperty = DependencyProperty.RegisterAttached(
        "Handler", typeof(EventHandler), typeof(ConsoleAutoScroll));

    public static RunSessionViewModel? GetSession(DependencyObject d) => (RunSessionViewModel?)d?.GetValue(SessionProperty);

    public static void SetSession(DependencyObject d, RunSessionViewModel? value) => d?.SetValue(SessionProperty, value);

    private static void OnSessionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list)
        {
            return;
        }

        if (e.OldValue is RunSessionViewModel old && list.GetValue(HandlerProperty) is EventHandler oldHandler)
        {
            old.LinesAppended -= oldHandler;
        }

        if (e.NewValue is RunSessionViewModel session)
        {
            EventHandler handler = (_, _) =>
            {
                if (session.AutoScroll)
                {
                    ScrollToEnd(list);
                }
            };
            session.LinesAppended += handler;
            list.SetValue(HandlerProperty, handler);
            list.Dispatcher.BeginInvoke(() => ScrollToEnd(list), System.Windows.Threading.DispatcherPriority.Background);
        }

        list.PreviewKeyDown -= OnKeyDown;
        list.PreviewKeyDown += OnKeyDown;
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is ListBox list && e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && list.SelectedItems.Count > 0)
        {
            var lines = list.SelectedItems.Cast<Tezgah.Core.Model.OutputLine>().OrderBy(l => list.Items.IndexOf(l)).Select(l => l.Text);
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, lines));
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
            }

            e.Handled = true;
        }
    }

    private static void ScrollToEnd(ListBox list)
    {
        if (FindScrollViewer(list) is { } sv)
        {
            sv.ScrollToBottom();
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
        {
            return sv;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
