using System.Reflection;
using System.Runtime.InteropServices;
using Tezgah.Core.Execution;

namespace Tezgah.Core.Tests.Execution;

public class DllSearchTests
{
    [Fact]
    public void Restrict_succeeds_and_can_be_repeated()
    {
        // Process-wide, but harmless for the test host: the runtime loads its native libraries by full path.
        Assert.True(DllSearch.Restrict());
        Assert.True(DllSearch.Restrict());
    }

    [Fact]
    public void Core_pinvokes_resolve_from_system32_only()
    {
        var attr = typeof(DllSearch).Assembly.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(DllImportSearchPath.System32, attr.Paths);
    }
}
