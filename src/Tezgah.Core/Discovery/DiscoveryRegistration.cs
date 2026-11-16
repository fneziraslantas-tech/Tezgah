using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Catalog;
using Tezgah.Core.Discovery;
using Tezgah.Core.Discovery.Detectors;

[assembly: InternalsVisibleTo("Tezgah.Core.Tests")]

namespace Tezgah.Core;

public static partial class TezgahCoreServices
{
    static partial void RegisterDiscovery(IServiceCollection services)
    {
        // Detectors: registration order does not matter, ProjectInspector applies its own default priority.
        services.AddSingleton<IProjectDetector, ScriptDetector>();
        services.AddSingleton<IProjectDetector, NodeDetector>();
        services.AddSingleton<IProjectDetector, PythonDetector>();
        services.AddSingleton<IProjectDetector, DotnetDetector>();
        services.AddSingleton<IProjectDetector, RustDetector>();
        services.AddSingleton<IProjectDetector, GoDetector>();
        services.AddSingleton<IProjectDetector, JavaDetector>();
        services.AddSingleton<IProjectDetector, DockerDetector>();
        services.AddSingleton<IProjectDetector, StaticWebDetector>();

        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<ProjectInspector>();

        services.AddSingleton<IDiscoveryProvider, FileSystemProvider>();
        services.AddSingleton<IDiscoveryProvider, RegistryAppsProvider>();
        services.AddSingleton<IDiscoveryProvider, PipxProvider>();
        services.AddSingleton<IDiscoveryProvider, NpmGlobalProvider>();
        services.AddSingleton<IDiscoveryProvider, CargoProvider>();
        services.AddSingleton<IDiscoveryProvider, DotnetToolProvider>();
        services.AddSingleton<IDiscoveryProvider, ScoopProvider>();
        services.AddSingleton<IDiscoveryProvider, GoBinProvider>();

        services.AddSingleton<ICatalogStore>(sp => new CatalogStore(sp.GetRequiredService<ILogger<CatalogStore>>()));
        services.AddSingleton<ICatalogService, CatalogService>();
    }
}
