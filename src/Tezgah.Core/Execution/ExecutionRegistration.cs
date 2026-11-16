using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Execution;
using Tezgah.Core.Safety;
using Tezgah.Core.Uninstall;

[assembly: InternalsVisibleTo("Tezgah.Core.Tests")]

namespace Tezgah.Core;

public static partial class TezgahCoreServices
{
    static partial void RegisterExecution(IServiceCollection services)
    {
        services.AddSingleton(_ => PathGuardOptions.FromEnvironment());
        services.AddSingleton(_ => UninstallOptions.FromEnvironment());

        services.AddSingleton<PathGuard>();
        services.AddSingleton<IPathGuard>(sp => sp.GetRequiredService<PathGuard>());
        services.AddSingleton<FileRemover>();
        services.AddSingleton<IFileRemover>(sp => sp.GetRequiredService<FileRemover>());

        services.AddSingleton<ICommandExecutor, CommandExecutor>();
        services.AddSingleton<IProcessHost, ProcessHost>();
        services.AddSingleton<IProcessFinder, ProcessFinder>();
        services.AddSingleton<IRunService, RunService>();
        services.AddSingleton<IUninstallService, UninstallService>();
        services.AddSingleton<ICleanupService, CleanupService>();
    }
}
