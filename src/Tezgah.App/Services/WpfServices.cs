using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace Tezgah.App.Services;

/// <summary>Reads strings from <see cref="Application.Resources"/> (the active Strings.*.xaml dictionary).</summary>
public sealed class ResourceStrings : IStrings
{
    public string Text(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    public string Format(string key, params object?[] args) =>
        string.Format(Tezgah.App.ViewModels.Formatting.Culture, Text(key), args);
}

public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    public IDisposable StartTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => tick(), _dispatcher);
        timer.Start();
        return new TimerHandle(timer);
    }

    private sealed class TimerHandle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}

public sealed class WpfClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // clipboard locked by another process; ignore
        }
    }
}

public sealed class AppearanceService : IAppearanceService
{
    private const string StringsPrefix = "Resources/Strings.";

    public void ApplyTheme(string theme)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        app.ThemeMode = theme switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    public void ApplyLanguage(string language)
    {
        var lang = language == "en" ? "en" : "tr";
        var culture = CultureInfo.GetCultureInfo(lang == "en" ? "en-US" : "tr-TR");
        Tezgah.App.ViewModels.Formatting.Culture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;

        // Setting the culture inside an async flow is undone when that flow returns; a dispatcher callback's
        // culture changes persist on the UI thread (WPF's culture-preserving execution context).
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
        });

        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged is null)
        {
            return;
        }

        var existing = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains(StringsPrefix, StringComparison.OrdinalIgnoreCase) == true);
        var replacement = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Tezgah;component/{StringsPrefix}{lang}.xaml", UriKind.Absolute) };
        if (existing is null)
        {
            merged.Add(replacement);
        }
        else if (!existing.Source!.OriginalString.EndsWith($".{lang}.xaml", StringComparison.OrdinalIgnoreCase))
        {
            merged[merged.IndexOf(existing)] = replacement;
        }
    }
}
