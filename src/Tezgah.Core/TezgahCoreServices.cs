using Microsoft.Extensions.DependencyInjection;

namespace Tezgah.Core;

/// <summary>
/// Composition root for the core. Each layer registers itself in its own partial file
/// (Discovery/DiscoveryRegistration.cs, Execution/ExecutionRegistration.cs), so layers stay decoupled.
/// </summary>
public static partial class TezgahCoreServices
{
    public static IServiceCollection AddTezgahCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        RegisterDiscovery(services);
        RegisterExecution(services);
        return services;
    }

    static partial void RegisterDiscovery(IServiceCollection services);

    static partial void RegisterExecution(IServiceCollection services);
}
