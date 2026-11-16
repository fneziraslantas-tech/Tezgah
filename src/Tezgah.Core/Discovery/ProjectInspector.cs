using Microsoft.Extensions.Logging;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.Core.Discovery;

/// <summary>Combined result of all detectors for one folder.</summary>
internal sealed record FolderDetection(
    IReadOnlyList<string> ProjectTypes,
    IReadOnlyList<RunProfile> Profiles,
    IReadOnlyList<ArtifactDir> Artifacts,
    string? Description)
{
    public bool IsProject => ProjectTypes.Count > 0;
}

/// <summary>
/// Runs every <see cref="IProjectDetector"/> plus README parsing on a folder and turns the result into a <see cref="CatalogItem"/>.
/// Shared by the filesystem provider and <c>CatalogService.AddFolderAsync</c>/<c>RefreshItemAsync</c>.
/// </summary>
internal sealed class ProjectInspector
{
    /// <summary>Order used to pick the single default profile when several ecosystems match.</summary>
    private static readonly string[] DefaultPriority = ["scripts", "node", "python", "dotnet", "rust", "go", "java", "docker", "static"];

    /// <summary>Files whose presence makes a folder worth running the detectors on (keeps the walker fast).</summary>
    private static readonly HashSet<string> MarkerFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "pyproject.toml", "requirements.txt", "setup.py", "setup.cfg", "Pipfile", "manage.py", "main.py", "app.py",
        "streamlit_app.py", "webui.py", "Cargo.toml", "go.mod", "pom.xml", "build.gradle", "build.gradle.kts", "compose.yaml",
        "compose.yml", "docker-compose.yml", "docker-compose.yaml", "Dockerfile", "index.html", "run.bat", "start.bat", "run.cmd",
        "start.cmd", "webui.bat", "webui-user.bat", "launch.bat", "run.ps1", "start.ps1", "launch.ps1",
    };

    private static readonly string[] MarkerExtensions = [".sln", ".slnx", ".csproj", ".fsproj", ".vbproj"];

    private readonly IReadOnlyList<IProjectDetector> _detectors;
    private readonly IGitClient _git;
    private readonly ILogger<ProjectInspector> _logger;

    public ProjectInspector(IEnumerable<IProjectDetector> detectors, IGitClient git, ILogger<ProjectInspector> logger)
    {
        _detectors = detectors.ToList();
        _git = git;
        _logger = logger;
    }

    /// <summary>Cheap pre-check on a directory listing: could any detector match?</summary>
    public static bool HasProjectMarker(string fileName) =>
        MarkerFiles.Contains(fileName) || MarkerExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Total time detectors may spend measuring artifact folders of one project.</summary>
    internal static readonly TimeSpan DetectionBudget = TimeSpan.FromSeconds(10);

    /// <summary>Runs the detectors (never executes project code). Cancellation is checked between and inside detectors.</summary>
    public FolderDetection Detect(string directory, bool includeReadmeCommands, CancellationToken ct = default)
    {
        using var scope = DetectionScope.Enter(DetectionBudget, ct);
        var detections = new List<ProjectDetection>();
        foreach (var detector in _detectors)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var d = detector.Detect(directory);
                if (d is not null)
                {
                    detections.Add(d);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException)
            {
                _logger.LogDebug(ex, "Detector {Detector} failed on {Path}", detector.ProjectType, directory);
            }
        }

        if (detections.Count > 1)
        {
            detections.RemoveAll(d => d.ProjectType == "static");
        }

        detections = detections.OrderBy(d => Priority(d.ProjectType)).ToList();

        var profiles = new List<RunProfile>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var defaultTaken = false;
        foreach (var d in detections)
        {
            foreach (var p in d.Profiles)
            {
                if (!ids.Add(p.Id))
                {
                    continue;
                }

                var isDefault = p.IsDefault && p.Kind == RunProfileKind.Run && !defaultTaken;
                defaultTaken |= isDefault;
                profiles.Add(p.IsDefault == isDefault ? p : p with { IsDefault = isDefault });
            }
        }

        string? description = detections.Select(d => d.Description).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        var readme = ReadmeParser.FindReadme(directory);
        string? readmeText = readme is null ? null : FsUtil.ReadText(readme, 512 * 1024);
        if (readmeText is not null)
        {
            description ??= ReadmeParser.ExtractDescription(readmeText);
            if (includeReadmeCommands || detections.Count > 0)
            {
                var n = 0;
                foreach (var cmd in ReadmeParser.ExtractCommands(readmeText))
                {
                    var id = $"readme:{++n}";
                    if (profiles.Any(p => SameCommand(p.Command, cmd.Tokens)))
                    {
                        continue; // already offered by a detector
                    }

                    if (ids.Add(id))
                    {
                        profiles.Add(FsUtil.Profile(id, cmd.Line, cmd.Kind, "readme",
                            new CommandSpec { FileName = cmd.Tokens[0], Arguments = cmd.Tokens.Skip(1).ToList(), WorkingDirectory = directory }));
                    }
                }
            }
        }

        var artifacts = detections.SelectMany(d => d.Artifacts).DistinctBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return new FolderDetection(detections.Select(d => d.ProjectType).Distinct().ToList(), profiles, artifacts, description);
    }

    private static bool SameCommand(CommandSpec spec, IReadOnlyList<string> tokens)
    {
        var program = Path.GetFileNameWithoutExtension(spec.FileName);
        return string.Equals(program, tokens[0], StringComparison.OrdinalIgnoreCase) &&
               spec.Arguments.SequenceEqual(tokens.Skip(1), StringComparer.Ordinal);
    }

    private static int Priority(string type)
    {
        var i = Array.IndexOf(DefaultPriority, type);
        return i < 0 ? DefaultPriority.Length : i;
    }

    /// <summary>
    /// Builds a catalog item for <paramref name="directory"/>. Returns null when it is neither a git repository nor a
    /// recognised project, unless <paramref name="force"/> (user picked the folder explicitly).
    /// <paramref name="skipCloudGit"/> (background scans) skips git for repositories whose git data is an online-only
    /// cloud placeholder: <c>git status</c> would make OneDrive download the whole object store and working tree.
    /// </summary>
    public async Task<CatalogItem?> InspectAsync(string directory, bool fetchGit, bool force, bool computeSize, CancellationToken ct, bool skipCloudGit = false)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(full))
        {
            return null;
        }

        var isGit = IsGitRoot(full);
        var detection = Detect(full, includeReadmeCommands: isGit || force, ct);
        if (!isGit && !detection.IsProject && !force)
        {
            return null;
        }

        GitInfo? git = null;
        if (isGit && skipCloudGit && IsCloudBackedRepo(full))
        {
            _logger.LogInformation("Online-only (cloud) repository {Path}: git details skipped during scan", full);
        }
        else if (isGit)
        {
            try
            {
                git = await _git.GetInfoAsync(full, fetchGit, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogInformation(ex, "Reading git info failed for {Path}", full);
            }
        }

        DateTimeOffset? created = null;
        try
        {
            created = Directory.GetCreationTimeUtc(full);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        var lastActivity = FsUtil.Max(git?.LastCommitAt, FsUtil.NewestTopLevelFileWrite(full));
        long? size = computeSize ? await Task.Run(() => FsUtil.DirectorySize(full, ct: ct), ct).ConfigureAwait(false) : null;
        var name = Path.GetFileName(full);
        return new CatalogItem
        {
            Id = ItemIds.ForPath(full),
            Kind = isGit ? ItemKind.GitRepository : ItemKind.ProjectFolder,
            Source = Sources.FileSystem,
            Name = string.IsNullOrEmpty(name) ? full : name,
            Path = full,
            Description = detection.Description,
            InstalledAt = created,
            LastActivity = lastActivity,
            SizeBytes = size,
            Git = git,
            ProjectTypes = detection.ProjectTypes,
            RunProfiles = detection.Profiles,
            Artifacts = detection.Artifacts,
            WebUrl = GitUrls.ToWebUrl(git?.RemoteUrl),
        };
    }

    /// <summary>
    /// True when the repository's git data is an online-only cloud placeholder (OneDrive "Files On-Demand"): reading it
    /// would trigger a download. Only attributes are read, never file contents.
    /// </summary>
    internal static bool IsCloudBackedRepo(string directory, Func<string, FileAttributes>? getAttributes = null)
    {
        var dotGit = Path.Combine(directory, ".git");
        if (FsUtil.IsCloudPlaceholder(dotGit, getAttributes))
        {
            return true;
        }

        if (!Directory.Exists(dotGit))
        {
            return false;
        }

        if (FsUtil.IsCloudPlaceholder(Path.Combine(dotGit, "index"), getAttributes) || FsUtil.IsCloudPlaceholder(Path.Combine(dotGit, "HEAD"), getAttributes))
        {
            return true;
        }

        try
        {
            var pack = Path.Combine(dotGit, "objects", "pack");
            return Directory.Exists(pack) && Directory.EnumerateFiles(pack, "*.pack").Take(4).Any(p => FsUtil.IsCloudPlaceholder(p, getAttributes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A <c>.git</c> directory, or a <c>.git</c> file (worktrees and submodules).</summary>
    public static bool IsGitRoot(string directory)
    {
        var dotGit = Path.Combine(directory, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }
}
