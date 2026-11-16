using System.Globalization;
using Tezgah.App.Demo;
using Tezgah.App.Services;
using Tezgah.App.ViewModels;
using Tezgah.Core.Abstractions;

namespace Tezgah.App.Tests;

internal sealed class KeyStrings : IStrings
{
    public string Text(string key) => key;

    public string Format(string key, params object?[] args) =>
        key + "(" + string.Join(",", args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture))) + ")";
}

internal sealed class InlineDispatcher : IUiDispatcher
{
    public List<Action> Timers { get; } = [];

    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public IDisposable StartTimer(TimeSpan interval, Action tick)
    {
        Timers.Add(tick);
        return new Noop();
    }

    public void Tick()
    {
        foreach (var t in Timers)
        {
            t();
        }
    }

    private sealed class Noop : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public bool TrustAnswer { get; set; }

    public bool ConfirmAnswer { get; set; } = true;

    public List<TrustPrompt> TrustPrompts { get; } = [];

    public Action<UninstallViewModel>? OnUninstall { get; set; }

    public bool Confirm(string title, string message, string confirmText, string cancelText, bool danger = false) => ConfirmAnswer;

    public void ShowInfo(string title, string message)
    {
    }

    public void ShowError(string title, string message)
    {
    }

    public bool ConfirmTrust(TrustPrompt prompt)
    {
        TrustPrompts.Add(prompt);
        return TrustAnswer;
    }

    public void ShowUninstall(UninstallViewModel viewModel) => OnUninstall?.Invoke(viewModel);

    public void ShowCleanup(CleanupViewModel viewModel)
    {
    }

    public void ShowSettings(SettingsViewModel viewModel)
    {
    }

    public void ShowClone(CloneViewModel viewModel)
    {
    }

    public List<AboutViewModel> AboutShown { get; } = [];

    public void ShowAbout(AboutViewModel viewModel) => AboutShown.Add(viewModel);

    public string? PickFolder(string title, string? initialDirectory) => null;
}

internal sealed class FakeShell : IShellService
{
    public List<Uri> Opened { get; } = [];

    public bool IsVsCodeAvailable => false;

    public bool OpenUrl(Uri url)
    {
        Opened.Add(url);
        return true;
    }

    public List<string> OpenedFolders { get; } = [];

    public bool OpenFolder(string path)
    {
        OpenedFolders.Add(path);
        return true;
    }

    public bool OpenTerminal(string path) => true;

    public bool OpenInVsCode(string path) => false;
}

internal sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }

    public void SetText(string text) => Text = text;
}

internal sealed class FakeAppearance : IAppearanceService
{
    public void ApplyTheme(string theme)
    {
    }

    public void ApplyLanguage(string language)
    {
    }
}

/// <summary>A MainViewModel wired to the demo services and UI fakes.</summary>
internal sealed class Harness
{
    public Harness(bool empty = false)
    {
        Catalog = new DemoCatalogService(new DemoOptions(empty));
        Runs = new DemoRunService(Catalog);
        Uninstall = new DemoUninstallService(Catalog, Runs);
        Vm = new MainViewModel(Catalog, Runs, Uninstall, new DemoCleanupService(Catalog), new DemoGitClient(Catalog),
            Dialogs, new FakeShell(), new FakeClipboard(), new KeyStrings(), Ui, new FakeAppearance(), new AppMode(true));
    }

    public DemoCatalogService Catalog { get; }

    public DemoRunService Runs { get; }

    public IUninstallService Uninstall { get; }

    public FakeDialogs Dialogs { get; } = new();

    public InlineDispatcher Ui { get; } = new();

    public MainViewModel Vm { get; }

    public ItemViewModel Item(string name) => Vm.AllItems.Single(i => i.Name == name);
}
