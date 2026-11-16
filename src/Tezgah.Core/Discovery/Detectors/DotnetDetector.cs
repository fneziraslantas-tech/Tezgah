using System.Xml;
using System.Xml.Linq;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.Core.Discovery.Detectors;

/// <summary>.NET solutions and projects (*.sln, *.slnx, *.csproj, *.fsproj, *.vbproj) at the root or up to two levels below.</summary>
internal sealed class DotnetDetector : IProjectDetector
{
    private static readonly string[] ProjectPatterns = ["*.csproj", "*.fsproj", "*.vbproj"];

    public string ProjectType => "dotnet";

    public ProjectDetection? Detect(string directory)
    {
        var solutions = FsUtil.TopLevelFiles(directory, "*.sln").Concat(FsUtil.TopLevelFiles(directory, "*.slnx")).ToList();
        var projects = FindProjects(directory);
        if (solutions.Count == 0 && projects.Count == 0)
        {
            return null;
        }

        var profiles = new List<RunProfile>();
        var artifacts = new List<ArtifactDir>();
        string? description = null;
        var buildTarget = solutions.Count > 0 ? Path.GetFileName(solutions[0]) : projects.Count > 0 ? Path.GetRelativePath(directory, projects[0]) : null;
        var runnable = new List<(string Path, bool IsWinExe)>();
        var hasTests = false;

        foreach (var project in projects)
        {
            var info = ReadProject(project);
            description ??= info.Description;
            hasTests |= info.IsTest;
            if (info.IsExe && !info.IsTest)
            {
                runnable.Add((project, info.IsWinExe));
            }

            var projectDir = Path.GetDirectoryName(project)!;
            artifacts.AddRange(FsUtil.Artifacts(projectDir, "dotnet", "bin", "obj"));
        }

        // Console/web apps before WinExe, shallower before deeper.
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (project, _) in runnable.OrderBy(r => r.IsWinExe).ThenBy(r => r.Path.Count(c => c == '\\')).Take(6))
        {
            var rel = Path.GetRelativePath(directory, project);
            var name = Path.GetFileNameWithoutExtension(project);

            // Keep the short id (saved DefaultProfileId values refer to it); same-named projects get a path-based id.
            var id = $"dotnet:run:{name.ToLowerInvariant()}";
            if (!usedIds.Add(id))
            {
                id = $"dotnet:run:{rel.Replace('\\', '/').ToLowerInvariant()}";
                usedIds.Add(id);
            }

            profiles.Add(FsUtil.Profile(id, $"dotnet run --project {rel}", RunProfileKind.Run, ProjectType,
                FsUtil.Spec("dotnet", directory, "run", "--project", rel), isDefault: profiles.Count == 0));
        }

        if (buildTarget is not null)
        {
            profiles.Add(FsUtil.Profile("dotnet:restore", "dotnet restore", RunProfileKind.Setup, ProjectType, FsUtil.Spec("dotnet", directory, "restore", buildTarget)));
            profiles.Add(FsUtil.Profile("dotnet:build", "dotnet build", RunProfileKind.Build, ProjectType, FsUtil.Spec("dotnet", directory, "build", buildTarget)));
            if (hasTests)
            {
                profiles.Add(FsUtil.Profile("dotnet:test", "dotnet test", RunProfileKind.Test, ProjectType, FsUtil.Spec("dotnet", directory, "test", buildTarget)));
            }
        }

        // Root-level bin/obj for solutions where the project sits at the root are already covered; dedupe by path.
        artifacts = artifacts.DistinctBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return new ProjectDetection(ProjectType, profiles, artifacts, description);
    }

    private static List<string> FindProjects(string directory)
    {
        var result = new List<string>();
        void AddFrom(string dir)
        {
            foreach (var pattern in ProjectPatterns)
            {
                result.AddRange(FsUtil.TopLevelFiles(dir, pattern));
            }
        }

        AddFrom(directory);
        foreach (var sub in FsUtil.TopLevelDirectories(directory).Where(d => !IsSkipped(d)).Take(200))
        {
            AddFrom(sub);
            if (result.Count >= 60)
            {
                break;
            }

            var name = Path.GetFileName(sub);
            if (name.Equals("src", StringComparison.OrdinalIgnoreCase) || name.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("test", StringComparison.OrdinalIgnoreCase) || name.Equals("samples", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var sub2 in FsUtil.TopLevelDirectories(sub).Where(d => !IsSkipped(d)).Take(200))
                {
                    AddFrom(sub2);
                }
            }
        }

        return result.Take(60).ToList();
    }

    private static bool IsSkipped(string dir)
    {
        var name = Path.GetFileName(dir);
        return name.StartsWith('.') || name is "bin" or "obj" or "node_modules" or "packages" or "artifacts";
    }

    private sealed record ProjectInfo(bool IsExe, bool IsWinExe, bool IsTest, string? Description);

    private static ProjectInfo ReadProject(string path)
    {
        var text = FsUtil.ReadText(path, 2 * 1024 * 1024);
        if (text is null)
        {
            return new ProjectInfo(false, false, false, null);
        }

        try
        {
            var doc = XDocument.Parse(text);
            var root = doc.Root;
            if (root is null)
            {
                return new ProjectInfo(false, false, false, null);
            }

            string? Prop(string name) => root.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            var sdk = root.Attribute("Sdk")?.Value ?? string.Empty;
            var outputType = Prop("OutputType");
            var isTest = string.Equals(Prop("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase) ||
                         root.Descendants().Any(e => e.Name.LocalName == "PackageReference" &&
                             string.Equals(e.Attribute("Include")?.Value, "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
            var isWinExe = string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase);
            var isExe = isWinExe || string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) ||
                        (outputType is null && (sdk.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
                                                sdk.Contains("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase)));
            return new ProjectInfo(isExe, isWinExe, isTest, Prop("Description"));
        }
        catch (XmlException)
        {
            return new ProjectInfo(false, false, false, null);
        }
    }
}
