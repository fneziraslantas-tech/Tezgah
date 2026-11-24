using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.Core.Discovery;

internal sealed class AiMcpProvider(ILogger<AiMcpProvider> logger) : IDiscoveryProvider
{
    public string Id => Sources.AiMcp;

    public Task<bool> IsAvailableAsync(CancellationToken ct) =>
        Task.FromResult(OllamaIsAvailable() || LmStudioIsAvailable() || VsCodeAiExtensionsIsAvailable() || McpConfigsExist());

    public async Task<IReadOnlyList<CatalogItem>> DiscoverAsync(TezgahSettings settings, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        try
        {
            var items = new List<CatalogItem>();
            progress?.Report(new ScanProgress(Id, null, 0));
            foreach (var item in DiscoverOllama(ct)) { ct.ThrowIfCancellationRequested(); items.Add(item); }
            foreach (var item in DiscoverLmStudio(ct)) { ct.ThrowIfCancellationRequested(); items.Add(item); }
            foreach (var item in DiscoverVsCodeAiExtensions(ct)) { ct.ThrowIfCancellationRequested(); items.Add(item); }
            foreach (var item in DiscoverMcpConfigs(ct)) { ct.ThrowIfCancellationRequested(); items.Add(item); }
            progress?.Report(new ScanProgress(Id, null, items.Count));
            return items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Provider} discovery failed", Id);
            return [];
        }
    }

    // ───────────────────────────── Ollama ─────────────────────────────

    private static bool OllamaIsAvailable()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Directory.Exists(Path.Combine(home, ".ollama")) ||
                   Cli.ResolveExecutable("ollama") is not null;
        }
        catch { return false; }
    }

    private static IEnumerable<CatalogItem> DiscoverOllama(CancellationToken ct)
    {
        var list = TryOllamaList(ct);
        if (list is not null) { foreach (var m in list) yield return m; yield break; }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var modelsDir = Path.Combine(home, ".ollama", "models");
        if (Directory.Exists(modelsDir))
        {
            foreach (var blobDir in FsUtil.TopLevelDirectories(modelsDir))
            {
                ct.ThrowIfCancellationRequested();
                var manifest = FsUtil.ReadText(Path.Combine(blobDir, "manifest.json"));
                if (manifest is null) continue;

                CatalogItem? item = null;
                try
                {
                    using var doc = JsonDocument.Parse(manifest);
                    var root = doc.RootElement;
                    var name = root.TryGetProperty("config", out var cfg) && cfg.TryGetProperty("Filename", out var fn)
                        ? fn.GetString() ?? Path.GetFileName(blobDir)
                        : Path.GetFileName(blobDir);
                    var size = root.TryGetProperty("config", out var cfg2) && cfg2.TryGetProperty("Size", out var sz)
                        ? sz.GetInt64() : 0L;
                    item = CreateItem("ollama:" + name, name, null, blobDir, null,
                        description: "Ollama model", web: "https://ollama.com/library",
                        sizeBytes: size > 0 ? size : null);
                }
                catch (JsonException) { }

                if (item is not null) yield return item;
            }
        }
    }

    private static List<CatalogItem>? TryOllamaList(CancellationToken ct)
    {
        try
        {
            var ollama = Cli.ResolveExecutable("ollama");
            if (ollama is null) return null;
            var result = Cli.RunAsync(ollama, ["list"], Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                TimeSpan.FromSeconds(30), ct).GetAwaiter().GetResult();
            if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut)) return null;
            var items = new List<CatalogItem>();
            var lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 1; i < lines.Length; i++)
            {
                var cols = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 3) continue;
                var name = cols[0];
                var size = ParseSize(cols[^2]);
                items.Add(CreateItem("ollama:" + name, name, null, null, null,
                    description: "Ollama model", web: $"https://ollama.com/library/{name}", sizeBytes: size));
            }
            return items.Count > 0 ? items : null;
        }
        catch { return null; }
    }

    private static long? ParseSize(string text)
    {
        try
        {
            var t = text.Trim();
            var mult = 1L;
            if (t.EndsWith("GB", StringComparison.OrdinalIgnoreCase)) mult = 1_073_741_824L;
            else if (t.EndsWith("MB", StringComparison.OrdinalIgnoreCase)) mult = 1_048_576L;
            else if (t.EndsWith("KB", StringComparison.OrdinalIgnoreCase)) mult = 1024L;
            var num = t.Replace("GB", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("MB", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("KB", "", StringComparison.OrdinalIgnoreCase).Trim();
            return long.Parse(num, System.Globalization.CultureInfo.InvariantCulture) * mult;
        }
        catch { return null; }
    }

    // ───────────────────────────── LM Studio ─────────────────────────────

    private static bool LmStudioIsAvailable()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Directory.Exists(Path.Combine(home, "AppData", "Local", "lm-studio")) ||
                   Directory.Exists(Path.Combine(home, ".lm-studio"));
        }
        catch { return false; }
    }

    private static IEnumerable<CatalogItem> DiscoverLmStudio(CancellationToken ct)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, "AppData", "Local", "lm-studio"),
            Path.Combine(home, ".lm-studio"),
        };

        foreach (var root in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;

            foreach (var dir in FsUtil.TopLevelDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(dir);
                if (string.Equals(name, "lm-studio", StringComparison.OrdinalIgnoreCase)) continue;

                var config = FsUtil.ReadText(Path.Combine(dir, "config.yaml")) ??
                             FsUtil.ReadText(Path.Combine(dir, "config.json"));

                yield return CreateItem("lmstudio:" + name, name, null, dir, null,
                    description: "LM Studio model", web: "https://lmstudio.ai/");
            }
        }
    }

    // ───────────────────────────── VS Code AI extensions ─────────────────────────────

    private static bool VsCodeAiExtensionsIsAvailable()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Directory.Exists(Path.Combine(home, ".vscode", "extensions"));
        }
        catch { return false; }
    }

    private static readonly HashSet<string> AiExtensionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.copilot", "github.copilot-chat", "continue.continue",
        "tabnine.tabnine-vscode", "sourcegraph.sourcegraph-cody",
        "codeium.codeium", "amazonq.amazonq",
    };

    private static IEnumerable<CatalogItem> DiscoverVsCodeAiExtensions(CancellationToken ct)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extDir = Path.Combine(home, ".vscode", "extensions");
        if (!Directory.Exists(extDir)) yield break;

        foreach (var dir in FsUtil.TopLevelDirectories(extDir))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            var parts = name.Split('.', 2);
            if (parts.Length < 2) continue;
            var extId = parts[0] + "." + parts[1];

            if (!AiExtensionIds.Contains(extId)) continue;

            var version = ReadVsixVersion(dir);
            yield return CreateItem("vscode:" + extId, name, version, dir, null,
                description: $"VS Code AI extension: {extId}",
                web: $"https://marketplace.visualstudio.com/items?itemName={extId}");
        }
    }

    private static string? ReadVsixVersion(string extDir)
    {
        try
        {
            var packageJson = Path.Combine(extDir, "package.json");
            if (!File.Exists(packageJson)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        catch { }
        return null;
    }

    // ───────────────────────────── MCP configs ─────────────────────────────

    private static bool McpConfigsExist()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return File.Exists(Path.Combine(home, ".mcp.json")) ||
                   File.Exists(Path.Combine(home, "AppData", "Roaming", "Claude", "claude_desktop_config.json"));
        }
        catch { return false; }
    }

    private static IEnumerable<CatalogItem> DiscoverMcpConfigs(CancellationToken ct)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var mcpJson = Path.Combine(home, ".mcp.json");
        if (File.Exists(mcpJson))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var item in ParseMcpJson(mcpJson, "user")) yield return item;
        }

        var claudeConfig = Path.Combine(home, "AppData", "Roaming", "Claude", "claude_desktop_config.json");
        if (File.Exists(claudeConfig))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var item in ParseClaudeConfig(claudeConfig)) yield return item;
        }
    }

    private static IEnumerable<CatalogItem> ParseMcpJson(string path, string sourceTag)
    {
        List<CatalogItem>? result = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object)
            {
                result = [];
                foreach (var server in servers.EnumerateObject())
                {
                    var cmd = server.Value.TryGetProperty("command", out var c) ? c.GetString() : null;
                    var args = server.Value.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                        ? string.Join(" ", a.EnumerateArray().Select(x => x.GetString() ?? string.Empty))
                        : null;
                    result.Add(CreateItem($"mcp:{sourceTag}:{server.Name}", server.Name, null, cmd,
                        UninstallMcp(server.Name, cmd, args),
                        description: $"MCP server ({sourceTag})" + (args is not null ? $" · {args}" : "")));
                }
            }
        }
        catch { }

        if (result is not null)
            foreach (var item in result) yield return item;
    }

    private static IEnumerable<CatalogItem> ParseClaudeConfig(string path)
    {
        List<CatalogItem>? result = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object)
            {
                result = [];
                foreach (var server in servers.EnumerateObject())
                {
                    var cmd = server.Value.TryGetProperty("command", out var c) ? c.GetString() : null;
                    var args = server.Value.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                        ? string.Join(" ", a.EnumerateArray().Select(x => x.GetString() ?? string.Empty))
                        : null;
                    result.Add(CreateItem($"mcp:claude:{server.Name}", server.Name, null, cmd,
                        UninstallMcp(server.Name, cmd, args),
                        description: "MCP server (Claude Desktop)" + (args is not null ? $" · {args}" : "")));
                }
            }
        }
        catch { }

        if (result is not null)
            foreach (var item in result) yield return item;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static CommandSpec? UninstallMcp(string name, string? command, string? args)
    {
        if (string.IsNullOrEmpty(command)) return null;
        var argList = args is null ? [] : args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new CommandSpec { FileName = command, Arguments = argList, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
    }

    private static CatalogItem CreateItem(string key, string name, string? version, string? path, CommandSpec? uninstall,
        string? description = null, string? web = null, long? sizeBytes = null) => new()
    {
        Id = ItemIds.ForSource(Sources.AiMcp, key),
        Kind = ItemKind.AiMcp,
        Source = Sources.AiMcp,
        Name = name,
        Version = version,
        Path = path,
        Description = description,
        UninstallCommand = uninstall,
        WebUrl = web,
        SizeBytes = sizeBytes,
        LastActivity = path is null ? null : SafeLastWrite(path),
        InstalledAt = path is null ? null : SafeLastWrite(path),
    };

    private static DateTimeOffset? SafeLastWrite(string path)
    {
        try
        {
            if (Directory.Exists(path)) return Directory.GetLastWriteTimeUtc(path);
            if (File.Exists(path)) return File.GetLastWriteTimeUtc(path);
        }
        catch { }
        return null;
    }
}