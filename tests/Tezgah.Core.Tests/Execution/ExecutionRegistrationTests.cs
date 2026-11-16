using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tezgah.Core.Abstractions;

namespace Tezgah.Core.Tests.Execution;

public class ExecutionRegistrationTests
{
    [Fact]
    public async Task All_execution_services_resolve()
    {
        var services = new ServiceCollection();
        services.AddTezgahCore();
        services.TryAddSingleton<ICatalogService>(new FakeCatalogService());
        services.TryAddSingleton<IGitClient>(new FakeGitClient(null));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<ICommandExecutor>());
        Assert.NotNull(provider.GetRequiredService<IProcessHost>());
        Assert.NotNull(provider.GetRequiredService<IPathGuard>());
        Assert.NotNull(provider.GetRequiredService<IFileRemover>());
        Assert.NotNull(provider.GetRequiredService<IRunService>());
        Assert.NotNull(provider.GetRequiredService<IUninstallService>());
        Assert.NotNull(provider.GetRequiredService<ICleanupService>());
        Assert.Same(provider.GetRequiredService<IPathGuard>(), provider.GetRequiredService<IPathGuard>());
    }
}
