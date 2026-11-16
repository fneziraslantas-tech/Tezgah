using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tezgah.App.Tests;

public class DllSearchHardeningTests
{
    [Fact]
    public void Dll_search_is_restricted_by_a_module_initializer_before_main()
    {
        var init = typeof(App).GetMethod(nameof(App.RestrictDllSearch), BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(init);
        Assert.NotNull(init.GetCustomAttribute<ModuleInitializerAttribute>());

        // Loading Tezgah.dll into the test host ran the initializer.
        Assert.True(App.DllSearchRestricted);
    }

    [Fact]
    public void App_pinvokes_resolve_from_system32_only()
    {
        var attr = typeof(App).Assembly.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(DllImportSearchPath.System32, attr.Paths);
    }
}
