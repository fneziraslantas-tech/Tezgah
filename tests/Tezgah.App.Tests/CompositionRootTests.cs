using Microsoft.Extensions.DependencyInjection;
using Tezgah.App.Services;
using Tezgah.App.ViewModels;
using Tezgah.Core.Abstractions;

namespace Tezgah.App.Tests;

/// <summary>
/// The real composition root (<see cref="App.ConfigureServices"/>) must build with every constructor dependency satisfied.
/// UI types needing a WPF Dispatcher (MainWindow, WpfUiDispatcher, MainViewModel) are only validated, not instantiated;
/// nothing here touches %LOCALAPPDATA%\Tezgah (no catalog is loaded, no file logger is attached).
/// </summary>
public class CompositionRootTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Composition_root_validates_and_resolves_services(bool forceDemo)
    {
        var services = new ServiceCollection();
        var (demo, coreMissing) = App.ConfigureServices(services, logProvider: null, forceDemo, demoEmpty: false);

        Assert.False(coreMissing);
        Assert.Equal(forceDemo, demo);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<ICatalogService>());
        Assert.NotNull(provider.GetRequiredService<IRunService>());
        Assert.NotNull(provider.GetRequiredService<IUninstallService>());
        Assert.NotNull(provider.GetRequiredService<ICleanupService>());
        Assert.NotNull(provider.GetRequiredService<IGitClient>());
        Assert.Equal(forceDemo, provider.GetRequiredService<AppMode>().IsDemo);

        // Registered (and validated above) but needs a running WPF Application to construct.
        Assert.Contains(services, d => d.ServiceType == typeof(MainViewModel));
        Assert.Contains(services, d => d.ServiceType == typeof(MainWindow));
        Assert.Contains(services, d => d.ServiceType == typeof(IUiDispatcher));
    }
}
