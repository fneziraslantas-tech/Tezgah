using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tezgah.App.Demo;
using Tezgah.App.Services;
using Tezgah.App.ViewModels;
using Tezgah.Core;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Execution;

namespace Tezgah.App;

/// <summary>
/// Composition root. Command line:
///   --demo          use sample data (fake catalog/run/uninstall/cleanup/git) even when real services exist
///   --demo-empty    demo mode starting with an empty catalog (shows the first-launch screen)
///   --theme T       light | dark | system for this session (does not change saved settings)
///   --lang L        tr | en for this session
///   --screenshot P  (Debug builds only) demo mode: render the main window to PNG file P after startup and exit
///   --screenshot-about P (with --screenshot) also render the About dialog to PNG file P
///   --export-logo D (Debug builds only) write tezgah.ico, logo.svg and per-size PNGs of the logo into folder D and exit
/// </summary>
public partial class App : Application, IDisposable
{
    private SingleInstance? _single;
    private ServiceProvider? _services;
    private FileLoggerProvider? _logProvider;
    private ILogger? _log;
    private int _crashDialogOpen;

    /// <summary>Result of <see cref="RestrictDllSearch"/>.</summary>
    internal static bool DllSearchRestricted { get; private set; }

    /// <summary>
    /// Runs when the exe's module loads, before Main creates the WPF application: every later native load (WPF's own
    /// DLLs, shell extensions, ours) uses the restricted search order (see <see cref="DllSearch.Restrict"/>).
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RestrictDllSearch() => DllSearchRestricted = DllSearch.Restrict();

    protected override async void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);
        var args = e.Args;
        if (DevLogoExportFolder(args) is { } logoFolder)
        {
            Shutdown(ExportLogo(logoFolder));
            return;
        }

        var screenshotPath = DevScreenshotPath(args);
        var demoEmpty = args.Contains("--demo-empty", StringComparer.OrdinalIgnoreCase);

        // The screenshot switch always uses the fake services: it never starts the user's real projects.
        var forceDemo = demoEmpty || screenshotPath is not null || args.Contains("--demo", StringComparer.OrdinalIgnoreCase);

        _logProvider = new FileLoggerProvider();
        HookGlobalExceptionHandlers();

        if (screenshotPath is null)
        {
            _single = new SingleInstance();
            if (!_single.IsFirst)
            {
                _single.SignalFirstInstance();
                Shutdown(0);
                return;
            }
        }

        var services = new ServiceCollection();
        var (demo, coreMissing) = ConfigureServices(services, _logProvider, forceDemo, demoEmpty);
        _services = services.BuildServiceProvider();
        _log = _services.GetRequiredService<ILoggerFactory>().CreateLogger("Tezgah.App");
        if (_log.IsEnabled(LogLevel.Information))
        {
            _log.LogInformation("Starting Tezgah {Version} (demo={Demo}, coreMissing={Missing})", typeof(App).Assembly.GetName().Version, demo, coreMissing);
        }

        if (!DllSearchRestricted)
        {
            _log.LogWarning("SetDefaultDllDirectories failed; the legacy DLL search order stays in effect");
        }

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        _single?.ListenForActivation(() => Dispatcher.BeginInvoke(window.BringToFront));

        var vm = _services.GetRequiredService<MainViewModel>();
        await vm.InitializeAsync();

        // Session-only overrides (handy for screenshots): --theme light|dark|system, --lang tr|en
        var appearance = _services.GetRequiredService<IAppearanceService>();
        if (ArgValue(args, "--theme") is { } theme)
        {
            appearance.ApplyTheme(theme.ToLowerInvariant());
        }

        if (ArgValue(args, "--lang") is { } lang)
        {
            appearance.ApplyLanguage(lang.ToLowerInvariant());
            vm.RefreshTexts();
        }

        if (screenshotPath is not null && _services.GetRequiredService<AppMode>().IsDemo)
        {
            await CaptureAsync(window, vm, screenshotPath, ArgValue(args, "--screenshot-about"));
        }
    }

    /// <summary>The developer screenshot switch exists only in Debug builds; Release ignores it.</summary>
    internal static string? DevScreenshotPath(string[] args)
    {
#if DEBUG
        return ArgValue(args, "--screenshot");
#else
        _ = args;
        return null;
#endif
    }

    /// <summary>The logo export switch (used by Assets/make-icon.ps1) exists only in Debug builds.</summary>
    internal static string? DevLogoExportFolder(string[] args)
    {
#if DEBUG
        return ArgValue(args, "--export-logo");
#else
        _ = args;
        return null;
#endif
    }

    /// <summary>
    /// Writes tezgah.ico, logo.svg and logo-N.png previews (16-256 px, each rendered separately) into <paramref name="folder"/>.
    /// Returns the process exit code.
    /// </summary>
    internal static int ExportLogo(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            using (var ico = File.Create(Path.Combine(folder, "tezgah.ico")))
            {
                Views.LogoArt.WriteIco(ico);
            }

            File.WriteAllText(Path.Combine(folder, "logo.svg"), Views.LogoArt.ToSvg());
            foreach (var size in Views.LogoArt.IconSizes)
            {
                File.WriteAllBytes(Path.Combine(folder, $"logo-{size}.png"), Views.LogoArt.Png(Views.LogoArt.Render(size)));
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Registers every service the app uses. Kept separate from <see cref="OnStartup"/> so tests can verify the
    /// composition root resolves without starting WPF. Returns whether demo services are used and whether the core was missing.
    /// </summary>
    internal static (bool Demo, bool CoreMissing) ConfigureServices(IServiceCollection services, ILoggerProvider? logProvider, bool forceDemo, bool demoEmpty)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging(b =>
        {
            if (logProvider is not null)
            {
                b.AddProvider(logProvider);
            }

            b.SetMinimumLevel(LogLevel.Information);
        });
        services.AddTezgahCore();
        var coreMissing = !services.Any(d => d.ServiceType == typeof(ICatalogService));
        var demo = forceDemo || coreMissing;
        if (demo)
        {
            services.AddTezgahDemo(replace: forceDemo, startEmpty: demoEmpty);
        }

        services.AddSingleton(new AppMode(demo));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IStrings, ResourceStrings>();
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<IClipboardService, WpfClipboardService>();
        services.AddSingleton<IAppearanceService, AppearanceService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return (demo, coreMissing);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log(ex, "Exit");
        }
        finally
        {
            base.OnExit(e);
        }
    }

    public void Dispose()
    {
        var services = _services;
        _services = null;
        try
        {
            if (services is not null)
            {
                DisposeContainer(services);
            }
        }
        finally
        {
            try
            {
                _single?.Dispose();
                _single = null;
            }
            finally
            {
                _logProvider?.Dispose();
                GC.SuppressFinalize(this);
            }
        }
    }

    /// <summary>
    /// Disposes the container asynchronously (RunService and friends are IAsyncDisposable; a synchronous
    /// ServiceProvider.Dispose throws for those) on the thread pool, so it cannot deadlock on the UI thread.
    /// </summary>
    internal static void DisposeContainer(ServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Task.Run(() => services.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(15));
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // ───────────────────────────── crash handling ─────────────────────────────

    private void HookGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Report(e.Exception, "Dispatcher");
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log(e.Exception, "UnobservedTask");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log(ex, "AppDomain");
            }
        };
    }

    private void Log(Exception ex, string source)
    {
        if (_log is not null)
        {
            _log.LogError(ex, "Unhandled exception ({Source})", source);
        }
        else
        {
            _logProvider?.Write(LogLevel.Error, "Tezgah.App", $"Unhandled exception ({source})", ex);
        }
    }

    private void Report(Exception ex, string source)
    {
        Log(ex, source);
        if (Interlocked.Exchange(ref _crashDialogOpen, 1) == 1)
        {
            return;
        }

        try
        {
            var strings = new ResourceStrings();
            var message = strings.Format("Error_Unexpected", ex.Message, _logProvider?.Directory ?? FileLoggerProvider.DefaultDirectory);
            new DialogService(strings).ShowError(strings.Text("Error_Title"), message);
        }
        catch (InvalidOperationException)
        {
            // dispatcher shutting down; already logged
        }
        finally
        {
            Volatile.Write(ref _crashDialogOpen, 0);
        }
    }

    // ───────────────────────────── dev screenshot ─────────────────────────────

    /// <summary>
    /// Selects a runnable trusted item, starts it, waits for output, renders the window to PNG (and, with
    /// <c>--screenshot-about P</c>, the About dialog to P) and exits.
    /// </summary>
    private async Task CaptureAsync(MainWindow window, MainViewModel vm, string path, string? aboutPath)
    {
        try
        {
            await Task.Delay(400);
            var target = vm.VisibleItems.FirstOrDefault(i => i.IsTrusted && i.HasProfiles && i.IsFavorite) ?? vm.VisibleItems.FirstOrDefault();
            if (target is not null)
            {
                vm.SelectedItem = target;
                if (target.IsTrusted && target.DefaultProfile is not null)
                {
                    await vm.RunCommand.ExecuteAsync(null);
                }
            }

            await Task.Delay(2600);
            SavePng(window, path);
            if (aboutPath is not null && _services is { } services)
            {
                // The About dialog, shown modeless next to the main window just long enough to be rendered.
                var about = new Views.AboutDialog(new AboutViewModel(services.GetRequiredService<IShellService>(), services.GetRequiredService<IStrings>(), FileLoggerProvider.DefaultDirectory))
                {
                    Owner = window,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ShowActivated = false,
                };
                about.Show();
                await Task.Delay(600);
                SavePng(about, aboutPath);
                about.Close();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log(ex, "Screenshot");
        }
        finally
        {
            var runs = _services?.GetService<IRunService>();
            if (runs is not null)
            {
                await runs.StopAllAsync(CancellationToken.None);
            }

            window.AllowCloseWithoutPrompt();
            Shutdown(0);
        }
    }

    /// <summary>Renders a window's content (on its own background) at the window's DPI and writes it as PNG.</summary>
    private void SavePng(Window window, string path)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var root = (FrameworkElement)window.Content;
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }

        bmp.Render(background);
        bmp.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var fs = File.Create(path))
        {
            encoder.Save(fs);
        }

        if (_log?.IsEnabled(LogLevel.Information) == true)
        {
            _log.LogInformation("Screenshot written to {Path}", Path.GetFullPath(path));
        }
    }
}