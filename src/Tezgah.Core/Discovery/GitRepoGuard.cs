namespace Tezgah.Core.Discovery;

/// <summary>
/// Managed pre-flight run before any git process touches a repository. git resolves <c>.git</c> files
/// (<c>gitdir:</c>), <c>commondir</c>, <c>objects/info/alternates</c> and config <c>include.path</c> entries before
/// any <c>-c</c> override applies, so a crafted repository could make a background scan open a UNC path and leak the
/// user's NTLM hash. Repositories whose git data is not on a local drive are refused (no git process is started).
/// </summary>
internal static class GitRepoGuard
{
    private const int MaxPointerFileBytes = 4096;
    private const long MaxConfigBytes = 256 * 1024;

    /// <summary>Returns true and the resolved git directory when every git data location of <paramref name="worktree"/> is local.</summary>
    public static bool TryResolveLocalGitDir(string worktree, out string? gitDir, out string? reason)
    {
        gitDir = null;
        reason = null;
        try
        {
            if (!IsLocalPath(worktree))
            {
                reason = "worktree is not on a local drive";
                return false;
            }

            var dotGit = Path.Combine(worktree, ".git");
            var attributes = File.GetAttributes(dotGit);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                reason = ".git is a reparse point";
                return false;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                gitDir = dotGit;
            }
            else
            {
                var target = ReadGitFilePointer(dotGit);
                if (target is null)
                {
                    reason = ".git file has no gitdir line";
                    return false;
                }

                var resolved = Resolve(worktree, target);
                if (resolved is null || !IsLocalPath(resolved))
                {
                    reason = "gitdir points to a non-local location";
                    return false;
                }

                gitDir = resolved;
            }

            if (!CheckGitDir(gitDir, out reason))
            {
                return false;
            }

            var commonDirFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonDirFile))
            {
                var raw = ReadSmall(commonDirFile)?.Split('\n')[0].Trim();
                var common = string.IsNullOrEmpty(raw) ? null : Resolve(gitDir, raw);
                if (common is null || !IsLocalPath(common) || !CheckGitDir(common, out reason))
                {
                    reason ??= "commondir points to a non-local location";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static bool CheckGitDir(string gitDir, out string? reason)
    {
        reason = null;
        var alternates = Path.Combine(gitDir, "objects", "info", "alternates");
        if (File.Exists(alternates))
        {
            foreach (var line in (ReadSmall(alternates) ?? string.Empty).Split('\n'))
            {
                var entry = line.Trim().Trim('"');
                if (entry.Length == 0 || entry.StartsWith('#'))
                {
                    continue;
                }

                var resolved = Resolve(Path.Combine(gitDir, "objects"), entry);
                if (resolved is null || !IsLocalPath(resolved))
                {
                    reason = "objects/info/alternates points to a non-local location";
                    return false;
                }
            }
        }

        if (File.Exists(Path.Combine(gitDir, "objects", "info", "http-alternates")))
        {
            reason = "http-alternates present";
            return false;
        }

        var config = Path.Combine(gitDir, "config");
        if (File.Exists(config) && new FileInfo(config).Length > MaxConfigBytes)
        {
            reason = "config file too large";
            return false;
        }

        foreach (var include in IncludePaths(ReadSmall(config, MaxConfigBytes)))
        {
            // Relative includes resolve against the config file's directory; "~/" against the profile.
            var expanded = include.StartsWith("~/", StringComparison.Ordinal) || include.StartsWith(@"~\", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), include[2..])
                : include;
            var resolved = Resolve(gitDir, expanded);
            if (resolved is null || !IsLocalPath(resolved))
            {
                reason = "config include points to a non-local location";
                return false;
            }
        }

        return true;
    }

    /// <summary>Values of <c>path</c> keys in <c>[include]</c>/<c>[includeIf ...]</c> sections (simple ini reading).</summary>
    internal static IEnumerable<string> IncludePaths(string? config)
    {
        if (config is null)
        {
            yield break;
        }

        var inInclude = false;
        foreach (var rawLine in config.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('['))
            {
                var end = line.IndexOf(']', StringComparison.Ordinal);
                var header = (end > 0 ? line[1..end] : line[1..]).Trim();
                inInclude = header.StartsWith("include", StringComparison.OrdinalIgnoreCase);
                line = end > 0 ? line[(end + 1)..].Trim() : string.Empty;
                if (line.Length == 0)
                {
                    continue;
                }
            }

            if (!inInclude)
            {
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !line[..eq].Trim().Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(eq + 1)..].Trim();
            var comment = value.IndexOfAny([';', '#']);
            if (comment >= 0 && !value.StartsWith('"'))
            {
                value = value[..comment].Trim();
            }

            yield return value.Trim('"');
        }
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

    private static string? Resolve(string baseDirectory, string value)
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

        if (v.StartsWith(@"\\", StringComparison.Ordinal) || v.StartsWith("//", StringComparison.Ordinal))
        {
            return v; // caller rejects it
        }

        try
        {
            return Path.GetFullPath(Path.IsPathRooted(v) ? v : Path.Combine(baseDirectory, v));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? ReadSmall(string path, long maxBytes = MaxPointerFileBytes)
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
