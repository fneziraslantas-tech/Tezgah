using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.Core.Discovery;

/// <summary>Shared plumbing for providers that list tools installed by a package manager CLI.</summary>
internal abstract class PackageToolProviderBase(ILogger logger) : IDiscoveryProvider
{
    protected static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(60);

    public abstract string Id { get; }

    /// <summary>The CLI that must be on PATH for this provider to run.</summary>
    protected abstract string Tool { get; }

    protected ILogger Logger { get; } = logger;

    protected static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public virtual Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Cli.ResolveExecutable(Tool) is not null);

    public async Task<IReadOnlyList<CatalogItem>> DiscoverAsync(TezgahSettings settings, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        try
        {
            var items = await DiscoverCoreAsync(ct).ConfigureAwait(false);
            progress?.Report(new ScanProgress(Id, null, items.Count));
            return items;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException)
        {
            Logger.LogWarning(ex, "{Provider} listing failed", Id);
            return [];
        }
    }

    protected abstract Task<IReadOnlyList<CatalogItem>> DiscoverCoreAsync(CancellationToken ct);

    protected async Task<string?> RunListAsync(string[] args, IReadOnlyDictionary<string, string>? env, CancellationToken ct)
    {
        var r = await Cli.RunAsync(Tool, args, Home, ListTimeout, ct, env).ConfigureAwait(false);
        if (!r.Success && r.StdOut.Length == 0)
        {
            Logger.LogInformation("{Tool} {Args} failed (exit {Code}): {Error}", Tool, string.Join(' ', args), r.ExitCode, r.StdErr.Split('\n')[0]);
            return null;
        }

        return r.StdOut;
    }

    protected CatalogItem CreateItem(string key, string name, string? version, string? path, CommandSpec? uninstall, string? description = null, string? web = null) => new()
    {
        Id = ItemIds.ForSource(Id, key),
        Kind = ItemKind.PackageTool,
        Source = Id,
        Name = name,
        Version = version,
        Path = path,
        Description = description,
        UninstallCommand = uninstall,
        WebUrl = web,
        LastActivity = path is null ? null : SafeLastWrite(path),
        InstalledAt = path is null ? null : SafeLastWrite(path),
    };

    protected static CommandSpec Uninstall(string tool, params string[] args) =>
        new() { FileName = tool, Arguments = args, WorkingDirectory = Home };

    private static DateTimeOffset? SafeLastWrite(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return Directory.GetLastWriteTimeUtc(path);
            }

            if (File.Exists(path))
            {
                return File.GetLastWriteTimeUtc(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }
}

/// <summary>pipx applications (<c>pipx list --json</c>).</summary>
internal sealed class PipxProvider(ILogger<PipxProvider> logger) : PackageToolProviderBase(logger)
{
    public override string Id => Sources.Pipx;

    protected override string Tool => "pipx";

    protected override async Task<IReadOnlyList<CatalogItem>> DiscoverCoreAsync(CancellationToken ct)
    {
        var json = await RunListAsync(["list", "--json"], null, ct).ConfigureAwait(false);
        return json is null ? [] : Parse(json);
    }

    internal IReadOnlyList<CatalogItem> Parse(string json)
    {
        var items = new List<CatalogItem>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("venvs", out var venvs) || venvs.ValueKind != JsonValueKind.Object)
        {
            return items;
        }

        foreach (var venv in venvs.EnumerateObject())
        {
            string? version = null;
            string name = venv.Name;
            string? path = null;
            if (venv.Value.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("main_package", out var main))
            {
                if (main.TryGetProperty("package", out var p) && p.ValueKind == JsonValueKind.String)
                {
                    name = p.GetString() ?? name;
                }

                if (main.TryGetProperty("package_version", out var v) && v.ValueKind == JsonValueKind.String)
                {
                    version = v.GetString();
                }

                if (main.TryGetProperty("app_paths", out var appPaths) && appPaths.ValueKind == JsonValueKind.Array)
                {
                    // app_paths: [{"__Path__": "C:\\Users\\x\\pipx\\venvs\\black\\Scripts\\black.exe", ...}] -> venv dir
                    var first = appPaths.EnumerateArray().Select(a => a.TryGetProperty("__Path__", out var pp) ? pp.GetString() : null).FirstOrDefault(s => s is not null);
                    var scripts = first is null ? null : Path.GetDirectoryName(first);
                    path = scripts is null ? null : Path.GetDirectoryName(scripts);
                }
            }

            items.Add(CreateItem(venv.Name, name, version, path, Uninstall("pipx", "uninstall", venv.Name), web: $"https://pypi.org/project/{venv.Name}/"));
        }

        return items;
    }
}

/// <summary>Global npm packages (<c>npm ls -g --depth=0 --json</c>).</summary>
internal sealed class NpmGlobalProvider(ILogger<NpmGlobalProvider> logger) : PackageToolProviderBase(logger)
{
    /// <summary>Uninstalling these would break the package manager itself.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase) { "npm", "corepack" };

    public override string Id => Sources.NpmGlobal;

    protected override string Tool => "npm";

    protected override async Task<IReadOnlyList<CatalogItem>> DiscoverCoreAsync(CancellationToken ct)
    {
        var json = await RunListAsync(["ls", "-g", "--depth=0", "--json"], null, ct).ConfigureAwait(false);
        if (json is null)
        {
            return [];
        }

        var root = (await RunListAsync(["root", "-g"], null, ct).ConfigureAwait(false))?.Trim();
        return Parse(json, string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root) ? null : root);
    }

    internal IReadOnlyList<CatalogItem> Parse(string json, string? globalRoot)
    {
        var items = new List<CatalogItem>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object)
        {
            return items;
        }

        foreach (var dep in deps.EnumerateObject())
        {
            if (Protected.Contains(dep.Name))
            {
                continue;
            }

            string? version = dep.Value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var path = globalRoot is null ? null : Path.Combine(globalRoot, dep.Name.Replace('/', Path.DirectorySeparatorChar));
            items.Add(CreateItem(dep.Name, dep.Name, version, path, Uninstall("npm", "uninstall", "-g", dep.Name), web: $"https://www.npmjs.com/package/{dep.Name}"));
        }

        return items;
    }
}

/// <summary>Binaries installed with <c>cargo install</c> (<c>cargo install --list</c>).</summary>
internal sealed partial class CargoProvider(ILogger<CargoProvider> logger) : PackageToolProviderBase(logger)
{
    public override string Id => Sources.Cargo;

    protected override string Tool => "cargo";

    protected override async Task<IReadOnlyList<CatalogItem>> DiscoverCoreAsync(CancellationToken ct)
    {
        var text = await RunListAsync(["install", "--list"], null, ct).ConfigureAwait(false);
        return text is null ? [] : Parse(text);
    }

    internal IReadOnlyList<CatalogItem> Parse(string text)
    {
        var items = new List<CatalogItem>();
        foreach (var line in text.Split('\n'))
        {
            var m = CrateLine().Match(line.TrimEnd('\r'));
            if (!m.Success)
            {
                continue;
            }

            var name = m.Groups["name"].Value;
            items.Add(CreateItem(name, name, m.Groups["version"].Value, null, Uninstall("cargo", "uninstall", name), web: $"https://crates.io/crates/{name}"));
        }

        return items;
    }

    /// <summary>"ripgrep v14.1.0:" or "mytool v0.1.0 (C:\src\mytool):"</summary>
    [GeneratedRegex(@"^(?<name>[A-Za-z0-9_\-]+) v(?<version>[^\s:]+)(?: \(.*\))?:\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CrateLine();
}

/// <summary>Global .NET tools (<c>dotnet tool list -g</c>).</summary>
internal sealed partial class DotnetToolProvider(ILogger<DotnetToolProvider> logger) : PackageToolProviderBase(logger)
{
    private static readonly Dictionary<string, string> EnglishOutput = new() { ["DOTNET_CLI_UI_LANGUAGE"] = "en", ["DOTNET_NOLOGO"] = "1", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" };

    public override string Id => Sources.DotnetTool;

    protected override string Tool => "dotnet";

    protected override async Task<IReadOnlyList<CatalogItem>> DiscoverCoreAsync(CancellationToken ct)
    {
        var text = await RunListAsync(["tool", "list", "-g"], EnglishOutput, ct).ConfigureAwait(false);
        return text is null ? [] : Parse(text);
    }

    internal IReadOnlyList<CatalogItem> Parse(string text)
    {
        var items = new List<CatalogItem>();
        var afterSeparator = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!afterSeparator)
            {
                afterSeparator = line.StartsWith("---", StringComparison.Ordinal);
                continue;
            }

            var cols = Columns().Split(line.Trim());
            if (cols.Length < 2 || cols[0].Length == 0)
            {
                continue;
            }

            var id = cols[0];
            var commands = cols.Length >= 3 ? cols[2] : null;
            items.Add(CreateItem(id, id, cols[1], null, Uninstall("dotnet", "tool", "uninstall", "-g", id),
                description: commands is null ? null : $"dotnet tool: {commands}", web: $"https://www.nuget.org/packages/{id}"));
        }

        return items;
    }

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Columns();
}

/// <summary>
/// Scoop apps. Read straight from <c>%SCOOP%\apps\*\current\manifest.json</c> (no PowerShell needed); uninstall runs <c>scoop uninstall</c>.
/// </summary>
internal sealed class ScoopProvider(ILogger<ScoopProvider> logger) : PackageToolProviderBase(logger)
{
    public override string Id => Sources.Scoop;

    protected override string Tool => "scoop";

    internal static string ScoopRoot => Environment.GetEnvironmentVariable("SCOOP") is { Length: > 0 } s ? s : Path.Combine(Home, "scoop");

    public override Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Directory.Exists(Path.Combine(ScoopRoot, "apps")));

    protected override Task<IReadOnlyList<CatalogItem>> DiscoverCoreAsync(CancellationToken ct) =>
        Task.FromResult(ReadApps(Path.Combine(ScoopRoot, "apps")));

    internal IReadOnlyList<CatalogItem> ReadApps(string appsDir)
    {
        var items = new List<CatalogItem>();
        foreach (var appDir in FsUtil.TopLevelDirectories(appsDir))
        {
            var name = Path.GetFileName(appDir);
            if (name.Equals("scoop", StringComparison.OrdinalIgnoreCase))
            {
                continue; // scoop itself
            }

            string? version = null, description = null, homepage = null;
            var manifest = FsUtil.ReadText(Path.Combine(appDir, "current", "manifest.json"));
            if (manifest is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(manifest, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    var r = doc.RootElement;
                    version = r.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    description = r.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    homepage = r.TryGetProperty("homepage", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
                }
                catch (JsonException)
                {
                }
            }

            items.Add(CreateItem(name, name, version, appDir, Uninstall("scoop", "uninstall", name), description, homepage));
        }

        return items;
    }
}

/// <summary>Go binaries installed with <c>go install</c> (files in GOBIN or %USERPROFILE%\go\bin). Removal = delete the file.</summary>
internal sealed class GoBinProvider(ILogger<GoBinProvider> logger) : PackageToolProviderBase(logger)
{
    public override string Id => Sources.GoBin;

    protected override string Tool => "go";

    internal static string? GoBinDirectory()
    {
        if (Environment.GetEnvironmentVariable("GOBIN") is { Length: > 0 } gobin)
        {
            return gobin;
        }

        var gopath = Environment.GetEnvironmentVariable("GOPATH");
        var first = gopath?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return Path.Combine(first ?? Path.Combine(Home, "go"), "bin");
    }

    public override Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(GoBinDirectory() is { } d && Directory.Exists(d));

    protected override Task<IReadOnlyList<CatalogItem>> DiscoverCoreAsync(CancellationToken ct)
    {
        var dir = GoBinDirectory();
        IReadOnlyList<CatalogItem> items = dir is null
            ? []
            : FsUtil.TopLevelFiles(dir, "*.exe")
                .Select(f => CreateItem(Path.GetFileName(f), Path.GetFileNameWithoutExtension(f), null, f, uninstall: null))
                .ToList();
        return Task.FromResult(items);
    }
}
