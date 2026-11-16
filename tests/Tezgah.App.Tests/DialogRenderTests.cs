using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tezgah.App.Services;
using Tezgah.App.ViewModels;
using Tezgah.App.Views;

namespace Tezgah.App.Tests;

/// <summary>
/// Smoke test: every dialog's XAML loads with the real application resources and renders without binding crashes.
/// Set TEZGAH_DIALOG_SHOTS=folder to also save PNGs of each dialog.
/// </summary>
public class DialogRenderTests
{
    [Fact]
    public void All_dialogs_load_and_render()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RenderAll();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "render thread timed out");
        Assert.Null(failure);
    }

    private static void RenderAll()
    {
        var app = Application.Current as App ?? new App();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.InitializeComponent();
        var strings = new ResourceStrings();
        var h = new Harness();
        h.Catalog.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        var items = h.Catalog.State.Items;
        var whisper = items.Single(i => i.Name == "whisper-transcriber");
        var vite = items.Single(i => i.Name == "vite-react-dashboard");
        var outDir = Environment.GetEnvironmentVariable("TEZGAH_DIALOG_SHOTS");

        Render(new ConfirmDialog("Tezgah kapatılıyor", "Çalışan 2 süreç durdurulsun mu?", "Durdur ve çık", "Vazgeç", ConfirmDialog.Kind.Danger), outDir, "confirm");
        Render(new TrustDialog(new TrustPrompt(whisper.Name, whisper.Path, whisper.Git?.RemoteUrl, whisper.RunProfiles[0].Command.Display), strings), outDir, "trust");
        Render(new UninstallDialog(new UninstallViewModel(whisper.Id, whisper.Name, h.Uninstall, strings)), outDir, "uninstall-danger");
        var webview = items.Single(i => i.Name.Contains("WebView2", StringComparison.Ordinal));
        Render(new UninstallDialog(new UninstallViewModel(webview.Id, webview.Name, h.Uninstall, strings)), outDir, "uninstall-blocked");
        Render(new CleanupDialog(new CleanupViewModel(vite, new Demo.DemoCleanupService(h.Catalog), strings)), outDir, "cleanup");
        Render(new SettingsDialog(new SettingsViewModel(h.Catalog.State.Settings, new FakeDialogs(), strings)), outDir, "settings");
        using var clone = new CloneViewModel(h.Catalog, new FakeDialogs(), strings, @"C:\Users\me\source\repos") { Url = "https://github.com/user/new-tool" };
        Render(new CloneDialog(clone), outDir, "clone");
        Render(new AboutDialog(new AboutViewModel(new FakeShell(), strings, FileLoggerProvider.DefaultDirectory)), outDir, "about");

        // Accessibility: glyph-only icon buttons are named by their tooltip; the typed-confirmation box has a label.
        var icon = new System.Windows.Controls.Button { Style = (Style)app.FindResource("IconButton"), Content = "\uE713", ToolTip = "Ayarlar" };
        var host = new Window { Content = icon, Width = 200, Height = 200, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        host.Show();
        Pump(TimeSpan.FromMilliseconds(300));
        Assert.Equal("Ayarlar", new System.Windows.Automation.Peers.ButtonAutomationPeer(icon).GetName());
        host.Close();
        var uninstall = new UninstallDialog(new UninstallViewModel(whisper.Id, whisper.Name, h.Uninstall, strings));
        Render(uninstall, null, "uninstall-a11y");
        Assert.NotNull(System.Windows.Automation.AutomationProperties.GetLabeledBy(uninstall.ConfirmBox));
    }

    private static void Render(Window w, string? outDir, string name)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -20000;
        w.Top = -20000;
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.Show();
        Pump(TimeSpan.FromMilliseconds(900));
        w.UpdateLayout();
        var root = (FrameworkElement)w.Content;
        Assert.True(root.ActualWidth > 100 && root.ActualHeight > 50, name + " has no size");
        if (!string.IsNullOrEmpty(outDir))
        {
            var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen())
            {
                dc.DrawRectangle(w.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }

            bmp.Render(bg);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            Directory.CreateDirectory(outDir);
            using var fs = File.Create(Path.Combine(outDir, name + ".png"));
            enc.Save(fs);
        }

        w.Close();
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(duration, DispatcherPriority.Background, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            frame.Continue = false;
        }, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
