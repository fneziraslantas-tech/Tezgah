using System.Collections.Concurrent;
using System.IO.Enumeration;
using Microsoft.Extensions.Logging;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Interop;
using Tezgah.Core.Model;
using Tezgah.Core.Safety;

namespace Tezgah.Core.Discovery;

/// <summary>
/// Walks the scan roots looking for git repositories and project folders.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Never follows reparse points (junctions, symlinks, mount points) and skips hidden/system and excluded directories.</item>
/// <item>A folder containing <c>.git</c> (directory, or file for worktrees/submodules) is a repository; the walk does not descend into it.</item>
/// <item>A folder with a "strong" project marker (package.json, pyproject.toml, *.csproj, Cargo.toml...) is a project. Below it
/// the walk continues for a couple of levels looking only for git repositories (a workspace folder with a docker-compose.yml
/// or .sln next to its sibling repos must not hide them). Below a repository only direct child repositories are looked for.
/// "Weak" markers (index.html, main.py, run.bat...) make the folder a candidate but the walk continues normally below it.</item>
/// <item>Tool-owned trees under the profile (Go module cache, scoop apps/buckets, conda, pipx) are skipped by path.</item>
/// <item>Each directory is listed exactly once; nothing is read except at candidate folders.</item>
/// </list>
/// </remarks>
internal sealed class FileSystemProvider(ProjectInspector inspector, ILogger<FileSystemProvider> logger) : IDiscoveryProvider
{
    private static readonly HashSet<string> StrongMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "pyproject.toml", "requirements.txt", "setup.py", "setup.cfg", "Pipfile", "manage.py", "Cargo.toml", "go.mod",
        "pom.xml", "build.gradle", "build.gradle.kts", "compose.yaml", "compose.yml", "docker-compose.yml", "docker-compose.yaml",
    };

    private static readonly string[] StrongExtensions = [".sln", ".slnx", ".csproj", ".fsproj", ".vbproj"];

    private static readonly string[] DevFolderNames = ["dev", "code", "projects", "repos", "src", "github", "source", "workspace", "git"];

    public string Id => Sources.FileSystem;

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public async Task<IReadOnlyList<CatalogItem>> DiscoverAsync(TezgahSettings settings, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var roots = ResolveRoots(settings);
        var candidates = await FindCandidatesAsync(roots, settings, progress, ct).ConfigureAwait(false);
        logger.LogInformation("Filesystem walk found {Count} candidate folders under {Roots} roots", candidates.Count, roots.Count);

        var items = new ConcurrentBag<CatalogItem>();
        var done = 0;
        var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
        await Parallel.ForEachAsync(candidates, parallel, async (path, token) =>
        {
            try
            {
                var item = await inspector.InspectAsync(path, fetchGit: false, force: false, computeSize: true, token, skipCloudGit: true).ConfigureAwait(false);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Inspecting {Path} failed", path);
            }

            var n = Interlocked.Increment(ref done);
            if (n % 10 == 0 || n == candidates.Count)
            {
                progress?.Report(new ScanProgress("inspect", path, items.Count));
            }
        }).ConfigureAwait(false);

        return items.DistinctBy(i => i.Id).OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Scan roots: settings, or sensible defaults (home, source\repos, Desktop, Documents, Downloads, dev folders on fixed drives).</summary>
    internal static List<string> ResolveRoots(TezgahSettings settings)
    {
        var roots = new List<string>();
        if (settings.ScanRoots.Count > 0)
        {
            roots.AddRange(settings.ScanRoots);
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(home);
            roots.Add(Path.Combine(home, "source", "repos"));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            roots.Add(Path.Combine(home, "Downloads"));
            foreach (var drive in SafeDrives())
            {
                foreach (var dir in FsUtil.TopLevelDirectories(drive))
                {
                    if (DevFolderNames.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase))
                    {
                        roots.Add(dir);
                    }
                }
            }
        }

        return roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r =>
            {
                try
                {
                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(r)));
                }
                catch (ArgumentException)
                {
                    return string.Empty;
                }
            })
            .Where(r => r.Length > 0 && Directory.Exists(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SafeDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var d in drives)
        {
            bool ok;
            try
            {
                ok = d.DriveType == DriveType.Fixed && d.IsReady;
            }
            catch (IOException)
            {
                ok = false;
            }

            if (ok)
            {
                yield return d.RootDirectory.FullName;
            }
        }
    }

    /// <summary>Home, Desktop, Documents, Downloads and drive roots are never catalog items themselves (a stray package.json in them must not turn the whole folder into a "project").</summary>
    private static bool IsContainerFolder(string dir)
    {
        if (Path.GetPathRoot(dir) is { } r && Path.TrimEndingDirectorySeparator(r).Equals(dir, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] containers =
        [
            home,
            Path.Combine(home, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        ];
        return containers.Any(c => c.Length > 0 && Path.TrimEndingDirectorySeparator(c).Equals(dir, StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct Entry(string Name, bool IsDirectory, FileAttributes Attributes);

    /// <summary>
    /// A file entry that is a link-like reparse point (symlink, possibly to a share). Cloud placeholders, dedup and WOF
    /// files are ordinary files; the tag is only looked up for reparse entries with a marker name.
    /// </summary>
    private static bool IsLinkedFile(string dir, Entry e) =>
        (e.Attributes & FileAttributes.ReparsePoint) != 0
        && (Win32.GetReparseTag(Path.Combine(dir, e.Name)) is not { } tag || !PathGuard.IsBenignReparseTag(tag));

    private static readonly Lazy<IReadOnlySet<string>> ToolCachePaths = new(() => BuildToolCachePaths(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable));

    /// <summary>True when <paramref name="path"/> is inside a tool-owned tree (never a user project).</summary>
    internal static bool IsUnderToolCache(string path)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return ToolCachePaths.Value.Any(p => full.Equals(p, StringComparison.OrdinalIgnoreCase) || full.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Folders owned by developer tools that look like projects but are caches: Go's module cache (GOPATH\pkg,
    /// GOMODCACHE), scoop apps/buckets/cache, conda installations, pipx venvs, pyenv. Matched by full path, not by
    /// name, because "go", "pkg" or "buckets" are also common names for real projects.
    /// </summary>
    internal static IReadOnlySet<string> BuildToolCachePaths(string home, Func<string, string?> env)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(params string?[] parts)
        {
            if (parts.Any(string.IsNullOrWhiteSpace))
            {
                return;
            }

            try
            {
                var p = Path.Combine(parts!);
                if (Path.IsPathFullyQualified(p))
                {
                    set.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)));
                }
            }
            catch (ArgumentException)
            {
            }
        }

        var gopath = env("GOPATH");
        string[] goRoots = string.IsNullOrWhiteSpace(gopath) ? [Path.Combine(home, "go")] : gopath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var g in goRoots)
        {
            Add(g, "pkg");
        }

        Add(env("GOMODCACHE"));
        foreach (var scoop in new[] { env("SCOOP") ?? Path.Combine(home, "scoop"), env("SCOOP_GLOBAL") })
        {
            foreach (var sub in new[] { "apps", "buckets", "cache", "persist", "shims" })
            {
                Add(scoop, sub);
            }
        }

        foreach (var conda in new[] { "miniconda3", "anaconda3", "Miniconda3", "Anaconda3", "miniforge3", "mambaforge" })
        {
            Add(home, conda);
        }

        Add(env("CONDA_PREFIX"));
        Add(env("PIPX_HOME") ?? Path.Combine(home, "pipx"));
        Add(home, ".pyenv");
        return set;
    }

    /// <summary>Walks all roots (in parallel) and returns candidate folders. Exposed for tests.</summary>
    internal async Task<List<string>> FindCandidatesAsync(IReadOnlyList<string> roots, TezgahSettings settings, IProgress<ScanProgress>? progress, CancellationToken ct, IReadOnlySet<string>? excludedPaths = null)
    {
        var excluded = new HashSet<string>(settings.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
        var excludedFull = excludedPaths ?? ToolCachePaths.Value;
        var rootSet = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        var found = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var visited = 0;
        var maxDepth = Math.Max(0, settings.MaxDepth);

        var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(roots.Count, 1, 4) };
        await Parallel.ForEachAsync(roots, parallel, (root, token) =>
        {
            Walk(root, maxDepth, excluded, excludedFull, rootSet, found, ref visited, progress, token);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        progress?.Report(new ScanProgress("walk", null, found.Count));
        return found.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void Walk(
        string root,
        int maxDepth,
        HashSet<string> excluded,
        IReadOnlySet<string> excludedPaths,
        HashSet<string> rootSet,
        ConcurrentDictionary<string, byte> found,
        ref int visited,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        // GitOnly > 0: below a project or repository, only git repositories are looked for, for that many more levels.
        var stack = new Stack<(string Path, int Depth, int GitOnly)>();
        stack.Push((root, 0, 0));
        var subdirs = new List<string>();
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth, gitOnly) = stack.Pop();
            var n = Interlocked.Increment(ref visited);
            if (n % 500 == 0)
            {
                progress?.Report(new ScanProgress("walk", dir, found.Count));
            }

            var hasGit = false;
            var strong = false;
            var weak = false;
            subdirs.Clear();
            try
            {
                var enumerable = new FileSystemEnumerable<Entry>(
                    dir,
                    (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Attributes),
                    new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false });
                foreach (var e in enumerable)
                {
                    if (e.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        hasGit = true;
                        continue;
                    }

                    if (!e.IsDirectory)
                    {
                        var isStrong = StrongMarkers.Contains(e.Name) || StrongExtensions.Any(x => e.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase));
                        if ((isStrong || (!weak && ProjectInspector.HasProjectMarker(e.Name))) && IsLinkedFile(dir, e))
                        {
                            continue; // a file symlink is no project marker: the detectors would not read it anyway
                        }

                        if (isStrong)
                        {
                            strong = true;
                        }
                        else if (!weak && ProjectInspector.HasProjectMarker(e.Name))
                        {
                            weak = true;
                        }

                        continue;
                    }

                    if ((e.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0 ||
                        excluded.Contains(e.Name))
                    {
                        continue;
                    }

                    var sub = Path.Combine(dir, e.Name);
                    if (excludedPaths.Contains(sub))
                    {
                        continue; // tool-owned tree (module cache, package manager buckets...)
                    }

                    subdirs.Add(sub);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                logger.LogDebug("Cannot list {Path}: {Error}", dir, ex.Message);
                continue;
            }

            var isRoot = depth == 0;
            if (gitOnly > 0)
            {
                // Only repositories count down here; markers of the enclosing project's own sub-packages do not.
                if (hasGit)
                {
                    found.TryAdd(dir, 0);
                    continue;
                }
            }
            else if ((hasGit || strong || weak) && !(isRoot && IsContainerFolder(dir)))
            {
                found.TryAdd(dir, 0);
            }

            int childGitOnly;
            if (gitOnly > 0)
            {
                childGitOnly = gitOnly - 1;
                if (childGitOnly == 0)
                {
                    continue;
                }
            }
            else if (!isRoot && hasGit)
            {
                childGitOnly = 1; // direct child repositories of a repository (e.g. a notes repo holding clones)
            }
            else if (!isRoot && strong)
            {
                childGitOnly = 2; // sibling repositories under a workspace folder (docker-compose.yml, .sln...)
            }
            else
            {
                childGitOnly = 0;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                if (rootSet.Contains(sub))
                {
                    continue; // walked separately with its own depth budget
                }

                stack.Push((sub, depth + 1, childGitOnly));
            }
        }
    }
}
