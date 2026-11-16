using Tezgah.Core.Interop;
using Tezgah.Core.Safety;

namespace Tezgah.Core.Discovery;

/// <summary>
/// Managed pre-flight run before any git process touches a repository. git resolves <c>.git</c> files (<c>gitdir:</c>),
/// <c>commondir</c>, <c>objects/info/alternates</c>, config includes and <c>core.worktree</c> before any <c>-c</c> override
/// applies, so a crafted repository could make git open a UNC path (leaking the user's NTLM hash) or read config the
/// hardening never saw.
/// </summary>
/// <remarks>
/// Two levels:
/// <list type="bullet">
/// <item><see cref="TryLocateGitDirs"/>: where the git data is, accepted only when local (fixed/removable/RAM drive, no
/// UNC/device/drive-relative/drive-root targets, no link in the chain from the worktree to it, a HEAD file present).
/// Enough for <see cref="GitMetadataReader"/>, which only reads files.</item>
/// <item><see cref="TryResolveLocalGitDir"/>: positive validation before a git process may run. On top of the location it
/// fails closed on anything it cannot fully parse or that redirects git elsewhere: config it cannot parse (value
/// continuation lines included), any include/includeIf, <c>core.worktree</c>, <c>extensions.worktreeConfig</c> /
/// <c>config.worktree</c>, UNC-looking config values, alternates outside local drives or chained alternates,
/// http-alternates, linked-worktree back-pointers (<c>worktrees/*/gitdir</c>) that are not local, submodule gitdirs outside the
/// repository, and link reparse points anywhere inside the git data.</item>
/// </list>
/// </remarks>
internal static class GitRepoGuard
{
    private const int MaxPointerFileBytes = 4096;
    private const long MaxConfigBytes = 256 * 1024;
    private const int MaxGitDirEntries = 100_000;
    private const int MaxWorktreeRegistrations = 100;

    /// <summary>
    /// Strict validation: true (and the per-worktree git directory) only when git may be run on <paramref name="worktree"/>.
    /// </summary>
    public static bool TryResolveLocalGitDir(string worktree, out string? gitDir, out string? reason)
    {
        gitDir = null;
        try
        {
            if (!TryLocateGitDirs(worktree, out var located, out var commonDir, out reason))
            {
                return false;
            }

            if (!CheckGitDirStrict(located!, out reason))
            {
                return false;
            }

            if (!string.Equals(located, commonDir, StringComparison.OrdinalIgnoreCase) && !CheckGitDirStrict(commonDir!, out reason))
            {
                return false;
            }

            if (!CheckWorktreeRegistrations(commonDir!, out reason))
            {
                return false;
            }

            if (!CheckSubmodules(Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree)), commonDir!, out reason))
            {
                return false;
            }

            gitDir = located;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Locates the per-worktree git directory and the common directory (refs, objects, config) of <paramref name="worktree"/>
    /// without trusting anything that points away from local drives. Reads at most the <c>.git</c> file and <c>commondir</c>.
    /// </summary>
    public static bool TryLocateGitDirs(string worktree, out string? gitDir, out string? commonDir, out string? reason)
    {
        gitDir = null;
        commonDir = null;
        reason = null;
        try
        {
            if (!IsLocalPath(worktree))
            {
                reason = "worktree is not on a local drive";
                return false;
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
            var dotGit = Path.Combine(root, ".git");

            // The worktree folder itself and its .git entry must not be links (a symlink can point at a share).
            if (Path.GetDirectoryName(root) is { } parent && FindLinkBelow(parent, dotGit) is { } link)
            {
                reason = $"link in the repository path: {link}";
                return false;
            }

            var attributes = File.GetAttributes(dotGit);
            string resolved;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                resolved = dotGit;
            }
            else
            {
                var target = ReadGitFilePointer(dotGit);
                if (target is null)
                {
                    reason = ".git file has no gitdir line";
                    return false;
                }

                if (Resolve(root, target) is not { } r || !IsAcceptableGitDirLocation(root, r, out reason))
                {
                    reason ??= "gitdir points to a non-local location";
                    return false;
                }

                resolved = r;
            }

            if (!File.Exists(Path.Combine(resolved, "HEAD")))
            {
                reason = "git directory has no HEAD";
                return false;
            }

            var common = resolved;
            var commonDirFile = Path.Combine(resolved, "commondir");
            if (File.Exists(commonDirFile))
            {
                var raw = ReadSmall(commonDirFile)?.Split('\n')[0].Trim();
                if (string.IsNullOrEmpty(raw) || Resolve(resolved, raw) is not { } c || !IsAcceptableGitDirLocation(resolved, c, out reason) || !Directory.Exists(c))
                {
                    reason ??= "commondir points to a non-local location";
                    return false;
                }

                common = c;
            }

            gitDir = resolved;
            commonDir = common;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>A git directory target: local, not a drive root, reachable from <paramref name="from"/> without links.</summary>
    private static bool IsAcceptableGitDirLocation(string from, string target, out string? reason)
    {
        reason = null;
        if (!IsLocalPath(target))
        {
            reason = "git data is not on a local drive";
            return false;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (string.Equals(Path.GetPathRoot(full)?.TrimEnd('\\', '/'), full.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            reason = "git data points to a drive root";
            return false;
        }

        if (FindLinkBelow(CommonAncestor(from, full), full) is { } link)
        {
            reason = $"link on the way to the git data: {link}";
            return false;
        }

        return true;
    }

    private static bool CheckGitDirStrict(string gitDir, out string? reason)
    {
        reason = null;
        var config = Path.Combine(gitDir, "config");
        if (File.Exists(config))
        {
            if (new FileInfo(config).Length > MaxConfigBytes)
            {
                reason = "config file too large";
                return false;
            }

            if (!GitConfigParser.TryParse(File.ReadAllText(config), strict: true, out var entries, out var error))
            {
                reason = $"config could not be fully parsed ({error})";
                return false;
            }

            foreach (var e in entries)
            {
                if (e.Section is "include" or "includeif")
                {
                    reason = "config uses include/includeIf";
                    return false;
                }

                if (e.Key is "core.worktree")
                {
                    reason = "config sets core.worktree";
                    return false;
                }

                if (e.Section == "extensions" && e.Name == "worktreeconfig")
                {
                    reason = "config enables per-worktree config";
                    return false;
                }

                if (e.Value is { } v && LooksRemote(v))
                {
                    reason = $"config value points to a network location ({e.Key})";
                    return false;
                }
            }
        }

        if (File.Exists(Path.Combine(gitDir, "config.worktree")))
        {
            reason = "per-worktree config present";
            return false;
        }

        if (File.Exists(Path.Combine(gitDir, "objects", "info", "http-alternates")))
        {
            reason = "http-alternates present";
            return false;
        }

        var alternates = Path.Combine(gitDir, "objects", "info", "alternates");
        if (File.Exists(alternates))
        {
            var text = ReadSmall(alternates);
            if (text is null)
            {
                reason = "alternates file unreadable";
                return false;
            }

            foreach (var line in text.Split('\n'))
            {
                var entry = line.Trim();
                if (entry.Length == 0 || entry.StartsWith('#'))
                {
                    continue;
                }

                if (entry.StartsWith('"'))
                {
                    reason = "quoted alternates entry";
                    return false;
                }

                var objects = Path.Combine(gitDir, "objects");
                if (Resolve(objects, entry) is not { } target || !IsLocalPath(target)
                    || FindLinkBelow(CommonAncestor(objects, target), target) is not null)
                {
                    reason = "objects/info/alternates points to a non-local location";
                    return false;
                }

                if (File.Exists(Path.Combine(target, "info", "alternates")) || File.Exists(Path.Combine(target, "info", "http-alternates")))
                {
                    reason = "chained alternates";
                    return false;
                }
            }
        }

        if (FindLinkInside(gitDir, out var tooLarge) is { } inner)
        {
            reason = $"link inside the git data: {inner}";
            return false;
        }

        if (tooLarge)
        {
            reason = "git data too large to validate";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Linked-worktree back-pointers (<c>&lt;commonDir&gt;/worktrees/&lt;id&gt;/gitdir</c> and <c>commondir</c>): commands such
    /// as <c>git worktree list</c> stat every registered worktree's path, and no config override prevents that. A path that
    /// is not on a local drive (UNC, <c>//host</c>, device, drive-relative) would make git open an SMB connection, so any
    /// entry that is missing, unreadable, too large or not provably local refuses the repository (fail closed).
    /// </summary>
    private static bool CheckWorktreeRegistrations(string commonDir, out string? reason)
    {
        reason = null;
        var root = Path.Combine(commonDir, "worktrees");
        if (File.Exists(root))
        {
            reason = "worktrees is a file";
            return false;
        }

        if (!Directory.Exists(root))
        {
            return true;
        }

        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var count = 0;
        foreach (var entry in new DirectoryInfo(root).EnumerateDirectories("*", options))
        {
            if (++count > MaxWorktreeRegistrations)
            {
                reason = "too many linked worktrees to validate";
                return false;
            }

            foreach (var (file, required) in new[] { ("gitdir", true), ("commondir", false) })
            {
                var path = Path.Combine(entry.FullName, file);
                if (!File.Exists(path))
                {
                    if (required)
                    {
                        reason = $"linked worktree '{entry.Name}' has no {file} file";
                        return false;
                    }

                    continue;
                }

                var value = ReadSmall(path)?.Split('\n')[0].Trim();
                if (string.IsNullOrEmpty(value) || LooksRemote(value) || Resolve(entry.FullName, value) is not { } target || LooksRemote(target) || !IsLocalPath(target))
                {
                    reason = $"linked worktree '{entry.Name}' {file} points to a non-local location";
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Submodule worktrees whose <c>.git</c> file points outside the repository are refused.</summary>
    private static bool CheckSubmodules(string worktree, string commonDir, out string? reason)
    {
        reason = null;
        var gitmodules = Path.Combine(worktree, ".gitmodules");
        if (!File.Exists(gitmodules))
        {
            return true;
        }

        if (FindLinkBelow(worktree, gitmodules) is not null || new FileInfo(gitmodules).Length > MaxConfigBytes)
        {
            reason = ".gitmodules is a link or too large";
            return false;
        }

        if (!GitConfigParser.TryParse(File.ReadAllText(gitmodules), strict: true, out var entries, out var error))
        {
            reason = $".gitmodules could not be fully parsed ({error})";
            return false;
        }

        foreach (var e in entries.Where(e => e.Section == "submodule" && e.Name == "path"))
        {
            var rel = e.Value ?? string.Empty;
            if (rel.Length == 0 || Path.IsPathRooted(rel) || rel.Split('/', '\\').Any(s => s is ".." || s.Contains(':', StringComparison.Ordinal)))
            {
                reason = $"invalid submodule path '{rel}'";
                return false;
            }

            var subRoot = Path.GetFullPath(Path.Combine(worktree, rel));
            var subGit = Path.Combine(subRoot, ".git");
            if (FindLinkBelow(worktree, subGit) is not null)
            {
                reason = $"link in submodule path '{rel}'";
                return false;
            }

            if (!File.Exists(subGit))
            {
                continue; // not checked out, or an embedded .git directory inside the worktree
            }

            var pointer = ReadGitFilePointer(subGit);
            if (pointer is null || Resolve(subRoot, pointer) is not { } target || !IsLocalPath(target)
                || !(PathGuard.IsSameOrUnder(target, commonDir) || PathGuard.IsSameOrUnder(target, worktree)))
            {
                reason = $"submodule '{rel}' keeps its git data outside the repository";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True for values git might treat as a network or NT-namespace path: UNC (\\host, //host, mixed slashes), the
    /// Win32 device and NT object-namespace prefixes (\\?\, \\.\, \??\, /??/), anything naming GLOBALROOT or the
    /// multiple UNC provider (Mup), and file://host URLs. Git for Windows hands pathname values (core.excludesFile,
    /// core.attributesFile...) straight to CreateFileW, which accepts all of these and would open an SMB/WebDAV share.
    /// </summary>
    internal static bool LooksRemote(string value)
    {
        var v = value.Trim();

        // "%(prefix)/" is git's runtime-prefix placeholder; judge what follows it.
        if (v.StartsWith("%(prefix)", StringComparison.OrdinalIgnoreCase))
        {
            v = v["%(prefix)".Length..];
        }

        var n = v.Replace('/', '\\');
        if (n.StartsWith(@"\\", StringComparison.Ordinal) || n.StartsWith(@"\??\", StringComparison.Ordinal)
            || n.Contains(@"\??\", StringComparison.Ordinal) || n.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase)
            || n.Contains(@"\Device\Mup", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return v.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && !v.StartsWith("file:///", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>First link-like reparse point among the path components of <paramref name="path"/> below <paramref name="ancestor"/>.</summary>
    internal static string? FindLinkBelow(string ancestor, string path)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ancestor));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!PathGuard.IsSameOrUnder(full, a))
        {
            a = Path.GetPathRoot(full) ?? a;
        }

        var current = a;
        foreach (var segment in full[a.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Win32.GetReparseTag(current) is { } tag && !PathGuard.IsBenignReparseTag(tag))
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>Bounded walk of a git directory for link-like reparse points (never followed).</summary>
    private static string? FindLinkInside(string gitDir, out bool tooLarge)
    {
        tooLarge = false;
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var stack = new Stack<string>();
        stack.Push(gitDir);
        var seen = 0;
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
            {
                if (++seen > MaxGitDirEntries)
                {
                    tooLarge = true;
                    return null;
                }

                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && FileRemover.IsLinkLike(entry))
                {
                    return entry.FullName;
                }

                if (entry is DirectoryInfo)
                {
                    stack.Push(entry.FullName);
                }
            }
        }

        return null;
    }

    private static string CommonAncestor(string a, string b)
    {
        var pa = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Split('\\');
        var pb = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)).Split('\\');
        var n = 0;
        while (n < pa.Length && n < pb.Length && string.Equals(pa[n], pb[n], StringComparison.OrdinalIgnoreCase))
        {
            n++;
        }

        if (n == 0)
        {
            return Path.GetPathRoot(Path.GetFullPath(b)) ?? b;
        }

        var joined = string.Join('\\', pa.Take(n));
        return joined.EndsWith(':') ? joined + "\\" : joined;
    }

    /// <summary>Parses the <c>gitdir: &lt;path&gt;</c> line of a <c>.git</c> file.</summary>
    internal static string? ReadGitFilePointer(string dotGitFile)
    {
        var text = ReadSmall(dotGitFile, MaxPointerFileBytes);
        if (text is null)
        {
            return null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["gitdir:".Length..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="path"/> is a fully qualified drive path (no UNC, device or extended prefix) on a
    /// fixed, removable or RAM drive (never a network share or mapped network drive).
    /// </summary>
    internal static bool IsLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            path.StartsWith(@"\??\", StringComparison.Ordinal) || path.StartsWith(@"/\", StringComparison.Ordinal) || path.StartsWith(@"\/", StringComparison.Ordinal))
        {
            return false;
        }

        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
        {
            return false;
        }

        if (path.IndexOf(':', 2) >= 0)
        {
            return false; // alternate data streams and the like
        }

        try
        {
            var type = new DriveInfo(path[..1]).DriveType;
            return type is DriveType.Fixed or DriveType.Removable or DriveType.Ram;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static string? Resolve(string baseDirectory, string value)
    {
        var v = value.Trim();
        if (v.Length == 0)
        {
            return null;
        }

        // git on Windows may write "/c/Users/..." style paths.
        if (v.Length >= 3 && v[0] == '/' && char.IsAsciiLetter(v[1]) && v[2] == '/')
        {
            v = $"{v[1]}:{v[2..]}";
        }

        if (v.StartsWith(@"\\", StringComparison.Ordinal) || v.StartsWith("//", StringComparison.Ordinal) || v.StartsWith(@"\/", StringComparison.Ordinal) || v.StartsWith(@"/\", StringComparison.Ordinal))
        {
            return v; // caller rejects it
        }

        // Drive-relative ("D:foo") and root-relative ("\foo") forms depend on process state: refuse.
        if ((v.Length >= 2 && v[1] == ':' && (v.Length == 2 || v[2] is not ('\\' or '/'))) || (v[0] is '\\' or '/' && !(v.Length >= 2 && v[1] == ':')))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.IsPathFullyQualified(v) ? v : Path.Combine(baseDirectory, v)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static string? ReadSmall(string path, long maxBytes = MaxPointerFileBytes)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > maxBytes)
            {
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
