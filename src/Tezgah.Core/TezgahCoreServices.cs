using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

        // Fallback so the core works without a logging host. Call AddLogging() BEFORE AddTezgahCore() to get real logs.
        services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(NullLogger<>)));

        RegisterDiscovery(services);
        RegisterExecution(services);
        return services;
    }

    static partial void RegisterDiscovery(IServiceCollection services);

    static partial void RegisterExecution(IServiceCollection services);
}
