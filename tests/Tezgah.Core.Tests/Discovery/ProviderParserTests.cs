using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Discovery;
using Tezgah.Core.Model;

namespace Tezgah.Core.Tests.Discovery;

public class RegistryEntryTests
{
    private static Func<string, object?> Values(Dictionary<string, object> d) => n => d.TryGetValue(n, out var v) ? v : null;

    [Fact]
    public void MapsTypicalEntry()
    {
        var item = RegistryAppsProvider.ReadEntry("hklm64", "Foo_is1", Values(new()
        {
            ["DisplayName"] = "Foo App",
            ["DisplayVersion"] = "1.2.3",
            ["Publisher"] = "Foo Inc",
            ["InstallLocation"] = "C:\\Program Files\\Foo\\",
            ["InstallDate"] = "20250131",
            ["EstimatedSize"] = 2048,
            ["URLInfoAbout"] = "https://foo.example",
            ["UninstallString"] = "\"C:\\Program Files\\Foo\\unins000.exe\"",
            ["QuietUninstallString"] = "\"C:\\Program Files\\Foo\\unins000.exe\" /SILENT",
        }))!;
        Assert.Equal(ItemKind.InstalledApp, item.Kind);
        Assert.Equal("registry:hklm64\\foo_is1", item.Id);
        Assert.Equal("Foo App", item.Name);
        Assert.Equal("1.2.3", item.Version);
        Assert.Equal(@"C:\Program Files\Foo", item.Path);
        Assert.Equal(new DateTime(2025, 1, 31), item.InstalledAt!.Value.Date);
        Assert.Equal(2048 * 1024L, item.SizeBytes);
        Assert.Equal("https://foo.example", item.WebUrl);
        Assert.Equal(@"C:\Program Files\Foo\unins000.exe", item.UninstallCommand!.FileName);
        Assert.Equal(["/SILENT"], item.UninstallCommand.Arguments);
        Assert.Equal(@"C:\Program Files\Foo", item.UninstallCommand.WorkingDirectory);
    }

    [Theory]
    [InlineData("SystemComponent")]
    [InlineData("ParentKeyName")]
    [InlineData("NoDisplayName")]
    [InlineData("KB")]
    public void SkipsHiddenEntries(string kind)
    {
        var d = new Dictionary<string, object> { ["DisplayName"] = "X", ["UninstallString"] = "x.exe" };
        var key = "X";
        switch (kind)
        {
            case "SystemComponent": d["SystemComponent"] = 1; break;
            case "ParentKeyName": d["ParentKeyName"] = "Office"; break;
            case "NoDisplayName": d.Remove("DisplayName"); break;
            case "KB": key = "KB5034441"; d["DisplayName"] = "Security Update for Windows (KB5034441)"; break;
        }

        Assert.Null(RegistryAppsProvider.ReadEntry("hklm64", key, Values(d)));
    }

    [Fact]
    public void MsiEntryGetsRemoveSwitch()
    {
        var item = RegistryAppsProvider.ReadEntry("hkcu", "{GUID}", Values(new()
        {
            ["DisplayName"] = "Msi App",
            ["UninstallString"] = "MsiExec.exe /I{11111111-2222-3333-4444-555555555555}",
            ["HelpLink"] = "not a url",
        }))!;
        Assert.Equal(["/X{11111111-2222-3333-4444-555555555555}"], item.UninstallCommand!.Arguments);
        Assert.Null(item.WebUrl);
        Assert.Null(item.Path);
    }

    [Fact]
    public async Task RealRegistryScanDoesNotThrow()
    {
        var p = new RegistryAppsProvider(NullLogger<RegistryAppsProvider>.Instance);
        var items = await p.DiscoverAsync(new TezgahSettings(), null, CancellationToken.None);
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Name)));
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct().Count());
    }
}

public class PackageParserTests
{
    [Fact]
    public void Pipx()
    {
        const string json = """
            { "pipx_spec_version": "0.1", "venvs": {
              "black": { "metadata": { "main_package": { "package": "black", "package_version": "24.1.0",
                 "app_paths": [ { "__type__": "Path", "__Path__": "C:\\Users\\u\\pipx\\venvs\\black\\Scripts\\black.exe" } ] } } } } }
            """;
        var item = Assert.Single(new PipxProvider(NullLogger<PipxProvider>.Instance).Parse(json));
        Assert.Equal("pipx:black", item.Id);
        Assert.Equal("24.1.0", item.Version);
        Assert.Equal(@"C:\Users\u\pipx\venvs\black", item.Path);
        Assert.Equal("pipx", item.UninstallCommand!.FileName);
        Assert.Equal(["uninstall", "black"], item.UninstallCommand.Arguments);
        Assert.Equal(ItemKind.PackageTool, item.Kind);
    }

    [Fact]
    public void NpmGlobalSkipsNpmItself()
    {
        const string json = """{ "dependencies": { "npm": { "version": "10.0.0" }, "@angular/cli": { "version": "17.0.1" }, "typescript": { "version": "5.4.0" } } }""";
        var items = new NpmGlobalProvider(NullLogger<NpmGlobalProvider>.Instance).Parse(json, @"C:\Users\u\AppData\Roaming\npm\node_modules");
        Assert.Equal(["@angular/cli", "typescript"], items.Select(i => i.Name));
        var ng = items[0];
        Assert.Equal(@"C:\Users\u\AppData\Roaming\npm\node_modules\@angular\cli", ng.Path);
        Assert.Equal(["uninstall", "-g", "@angular/cli"], ng.UninstallCommand!.Arguments);
    }

    [Fact]
    public void Cargo()
    {
        const string text = "ripgrep v14.1.0:\n    rg.exe\nmytool v0.1.0 (C:\\src\\mytool):\n    mytool.exe\n";
        var items = new CargoProvider(NullLogger<CargoProvider>.Instance).Parse(text);
        Assert.Equal(["ripgrep", "mytool"], items.Select(i => i.Name));
        Assert.Equal("14.1.0", items[0].Version);
        Assert.Null(items[0].Path); // shared bin folder must never be a delete target
        Assert.Equal(["uninstall", "ripgrep"], items[0].UninstallCommand!.Arguments);
    }

    [Fact]
    public void DotnetTools()
    {
        const string text = "Package Id                Version      Commands\n-------------------------------------------------\ndotnet-ef                 8.0.1        dotnet-ef\ncsharpier                 0.27.0       dotnet-csharpier\n";
        var items = new DotnetToolProvider(NullLogger<DotnetToolProvider>.Instance).Parse(text);
        Assert.Equal(["dotnet-ef", "csharpier"], items.Select(i => i.Name));
        Assert.Equal("8.0.1", items[0].Version);
        Assert.Equal(["tool", "uninstall", "-g", "dotnet-ef"], items[0].UninstallCommand!.Arguments);
    }

    [Fact]
    public void ScoopReadsManifests()
    {
        using var dir = new TempDir();
        dir.File(@"apps\7zip\current\manifest.json", """{ "version": "23.01", "description": "Archiver", "homepage": "https://7-zip.org" }""");
        dir.Sub("apps", "scoop", "current");
        var items = new ScoopProvider(NullLogger<ScoopProvider>.Instance).ReadApps(Path.Combine(dir.Path, "apps"));
        var item = Assert.Single(items);
        Assert.Equal("7zip", item.Name);
        Assert.Equal("23.01", item.Version);
        Assert.Equal("https://7-zip.org", item.WebUrl);
        Assert.Equal(["uninstall", "7zip"], item.UninstallCommand!.Arguments);
    }
}

public class CliTests
{
    [Theory]
    [InlineData("safe-arg", true)]
    [InlineData("with space", true)]
    [InlineData("a&b", false)]
    [InlineData("a|b", false)]
    [InlineData("a>b", false)]
    [InlineData("%PATH%", false)]
    [InlineData("a^b", false)]
    [InlineData("\"q\"", false)]
    [InlineData("bang!", false)]
    [InlineData("line\nbreak", false)]
    public void CmdSafety(string arg, bool safe) => Assert.Equal(safe, Cli.IsSafeForCmd(arg));

    [Fact]
    public void BatchFilesWithUnsafeArgsAreRefused()
    {
        using var dir = new TempDir();
        var bat = dir.File("tool.cmd", "@echo %*");
        Assert.Null(Cli.CreateStartInfo(bat, ["ok", "x & calc"], dir.Path));
        var psi = Cli.CreateStartInfo(bat, ["ok", "two words"], dir.Path)!;
        Assert.EndsWith("cmd.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"/d /s /c \"\"{bat}\" ok \"two words\"\"", psi.Arguments);
        Assert.False(psi.UseShellExecute);
    }

    [Fact]
    public void ExecutablesUseArgumentList()
    {
        var psi = Cli.CreateStartInfo(@"C:\Windows\System32\where.exe", ["a b", "c&d"], null)!;
        Assert.Equal(["a b", "c&d"], psi.ArgumentList);
        Assert.Equal(string.Empty, psi.Arguments);
    }

    [Fact]
    public async Task RunsBatchFileThroughCmdAndCapturesOutput()
    {
        using var dir = new TempDir();
        var bat = dir.File("echoargs.cmd", "@echo off\r\necho [%1] [%~2]\r\n");
        var r = await Cli.RunAsync(bat, ["one", "two words"], dir.Path, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.True(r.Success, r.StdErr);
        Assert.Equal("[one] [two words]", r.StdOut.Trim());
    }

    [Fact]
    public async Task MissingToolReturnsFailure()
    {
        var r = await Cli.RunAsync("definitely-not-a-real-tool-xyz", [], null, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(r.Success);
    }

    [Fact]
    public void RelativePathsAreNeverResolved() => Assert.Null(Cli.ResolveExecutable(@".\tool.exe"));
}
