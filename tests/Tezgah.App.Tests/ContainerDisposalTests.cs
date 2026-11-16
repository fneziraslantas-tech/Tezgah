using Microsoft.Extensions.DependencyInjection;
using Tezgah.Core;
using Tezgah.Core.Abstractions;

namespace Tezgah.App.Tests;

/// <summary>The real container must dispose cleanly at app exit (RunService is async-disposable).</summary>
public class ContainerDisposalTests
{
    private static ServiceProvider BuildRealContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTezgahCore();
        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IRunService>()); // resolved singletons are what disposal has to handle
        Assert.NotNull(sp.GetRequiredService<ICatalogService>());
        return sp;
    }

    [Fact]
    public void App_exit_path_disposes_the_real_container()
    {
        var sp = BuildRealContainer();
        App.DisposeContainer(sp);
    }

    [Fact]
    public void Synchronous_dispose_no_longer_throws()
    {
        var sp = BuildRealContainer();
        sp.Dispose();
    }

    [Fact]
    public void Screenshot_switch_is_debug_only()
    {
#if DEBUG
        Assert.Equal("x.png", App.DevScreenshotPath(["--screenshot", "x.png"]));
#else
        Assert.Null(App.DevScreenshotPath(["--screenshot", "x.png"]));
#endif
    }
}
