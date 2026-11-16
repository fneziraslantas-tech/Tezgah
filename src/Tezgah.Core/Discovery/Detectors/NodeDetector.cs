using System.Text.Json;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.Core.Discovery.Detectors;

/// <summary>package.json based projects (npm, pnpm, yarn, bun).</summary>
internal sealed class NodeDetector : IProjectDetector
{
    private static readonly string[] RunScripts = ["dev", "start", "serve", "preview"];

    public string ProjectType => "node";

    public ProjectDetection? Detect(string directory)
    {
        var pkgPath = Path.Combine(directory, "package.json");
        var text = FsUtil.ReadText(pkgPath, 1024 * 1024);
        if (text is null)
        {
            return null;
        }

        string? description = null;
        string? packageManagerField = null;
        var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
            {
                description = d.GetString();
            }

            if (root.TryGetProperty("packageManager", out var pm) && pm.ValueKind == JsonValueKind.String)
            {
                packageManagerField = pm.GetString();
            }

            if (root.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in s.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        scripts[p.Name] = p.Value.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Malformed package.json: still a node project, just without scripts.
        }

        var pmName = DetectPackageManager(directory, packageManagerField);
        var profiles = new List<RunProfile>
        {
            FsUtil.Profile($"{pmName}:install", $"{pmName} install", RunProfileKind.Setup, ProjectType, FsUtil.Spec(pmName, directory, "install")),
        };

        string? defaultScript = RunScripts.FirstOrDefault(scripts.ContainsKey);
        foreach (var name in RunScripts.Where(scripts.ContainsKey))
        {
            profiles.Add(ScriptProfile(directory, pmName, name, RunProfileKind.Run, name == defaultScript));
        }

        if (scripts.ContainsKey("build"))
        {
            profiles.Add(ScriptProfile(directory, pmName, "build", RunProfileKind.Build, false));
        }

        if (scripts.ContainsKey("test"))
        {
            profiles.Add(ScriptProfile(directory, pmName, "test", RunProfileKind.Test, false));
        }

        var artifacts = FsUtil.Artifacts(directory, "node", "node_modules", "dist", ".next", ".nuxt", "build", ".svelte-kit", ".turbo", ".parcel-cache");
        return new ProjectDetection(ProjectType, profiles, artifacts, string.IsNullOrWhiteSpace(description) ? null : description.Trim());
    }

    private RunProfile ScriptProfile(string directory, string pm, string script, RunProfileKind kind, bool isDefault) =>
        FsUtil.Profile($"{pm}:{script}", $"{pm} run {script}", kind, ProjectType, FsUtil.Spec(pm, directory, "run", script), isDefault);

    internal static string DetectPackageManager(string directory, string? packageManagerField)
    {
        if (!string.IsNullOrEmpty(packageManagerField))
        {
            foreach (var known in new[] { "pnpm", "yarn", "bun", "npm" })
            {
                if (packageManagerField.StartsWith(known + "@", StringComparison.OrdinalIgnoreCase) || packageManagerField.Equals(known, StringComparison.OrdinalIgnoreCase))
                {
                    return known;
                }
            }
        }

        if (File.Exists(Path.Combine(directory, "pnpm-lock.yaml")))
        {
            return "pnpm";
        }

        if (File.Exists(Path.Combine(directory, "yarn.lock")))
        {
            return "yarn";
        }

        if (File.Exists(Path.Combine(directory, "bun.lockb")) || File.Exists(Path.Combine(directory, "bun.lock")))
        {
            return "bun";
        }

        return "npm";
    }
}
