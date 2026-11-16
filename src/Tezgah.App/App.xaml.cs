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

namespace Tezgah.App;

/// <summary>
/// Composition root. Command line:
///   --demo          use sample data (fake catalog/run/uninstall/cleanup/git) even when real services exist
///   --demo-empty    demo mode starting with an empty catalog (shows the first-launch screen)
///   --theme T       light | dark | system for this session (does not change saved settings)
///   --lang L        tr | en for this session
///   --screenshot P  (dev) render the main window to PNG file P after startup and exit
/// </summary>
public partial class App : Application, IDisposable
{
    private SingleInstance? _single;
    private ServiceProvider? _services;
    private FileLoggerProvider? _logProvider;
    private ILogger? _log;
    private int _crashDialogOpen;

    protected override async void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);
        var args = e.Args;
        var screenshotPath = ArgValue(args, "--screenshot");
        var demoEmpty = args.Contains("--demo-empty", StringComparer.OrdinalIgnoreCase);
        var forceDemo = demoEmpty || args.Contains("--demo", StringComparer.OrdinalIgnoreCase);

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
        services.AddLogging(b => b.AddProvider(_logProvider).SetMinimumLevel(LogLevel.Information));
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
        _services = services.BuildServiceProvider();
        _log = _services.GetRequiredService<ILoggerFactory>().CreateLogger("Tezgah.App");
        if (_log.IsEnabled(LogLevel.Information))
        {
            _log.LogInformation("Starting Tezgah {Version} (demo={Demo}, coreMissing={Missing})", typeof(App).Assembly.GetName().Version, demo, coreMissing);
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

        if (screenshotPath is not null)
        {
            await CaptureAsync(window, vm, screenshotPath);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        _services?.Dispose();
        _services = null;
        _single?.Dispose();
        _single = null;
        _logProvider?.Dispose();
        GC.SuppressFinalize(this);
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

    /// <summary>Selects a runnable trusted item, starts it, waits for output, renders the window to PNG and exits.</summary>
    private async Task CaptureAsync(MainWindow window, MainViewModel vm, string path)
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
            window.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(window);
            var root = (FrameworkElement)window.Content;
            var bmp = new RenderTargetBitmap(
                (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
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
}
