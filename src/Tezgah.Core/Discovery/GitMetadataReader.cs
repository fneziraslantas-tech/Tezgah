using System.Globalization;
using System.IO.Compression;
using System.Text;
using Tezgah.Core.Model;

namespace Tezgah.Core.Discovery;

/// <summary>
/// Reads a repository's git metadata directly from its git directory, without starting git. Used for every repository the
/// user has not trusted (background scans, catalog refresh) and as the fail-closed fallback when <see cref="GitRepoGuard"/>
/// cannot positively validate a repository for the git CLI.
/// </summary>
/// <remarks>
/// Nothing here executes or follows repository-controlled settings: <c>.git</c> file and <c>commondir</c> pointers are
/// accepted only when local (<see cref="GitRepoGuard.TryLocateGitDirs"/>), config is parsed tolerantly but never acted upon
/// (includes ignored), every file is read with a size cap and only after checking that no link sits between the git
/// directory and it. What cannot be known without git (modified/untracked files, commits on other branches) is reported as
/// unknown via <see cref="GitInfo.StatusUnknown"/> / <see cref="GitInfo.UnpushedCommits"/> = null, which counts as unsaved
/// work for deletion safety.
/// </remarks>
internal static class GitMetadataReader
{
    private const long MaxSmallFile = 64 * 1024;
    private const long MaxConfigBytes = 256 * 1024;
    private const long MaxPackedRefs = 16 * 1024 * 1024;
    private const int MaxCommitBytes = 256 * 1024;

    /// <summary>Loose commits walked when looking for the upstream commit in the local branch's history (and back).</summary>
    internal const int MaxAncestryWalk = 200;

    /// <summary>Null when <paramref name="worktree"/> is not a repository whose git data could be located safely.</summary>
    public static GitInfo? Read(string worktree)
    {
        if (!GitRepoGuard.TryLocateGitDirs(worktree, out var gitDir, out var commonDir, out _))
        {
            return null;
        }

        try
        {
            return new Repo(gitDir!, commonDir!).Read(Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException or FormatException)
        {
            return null;
        }
    }

    /// <summary>A Unix timestamp plus "+hhmm" offset as written in commit headers and reflogs.</summary>
    internal static DateTimeOffset? ParseGitTime(string seconds, string zone)
    {
        if (!long.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) || zone.Length != 5 || zone[0] is not ('+' or '-')
            || !int.TryParse(zone.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hh)
            || !int.TryParse(zone.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var mm) || hh > 14 || mm > 59)
        {
            return null;
        }

        try
        {
            var offset = new TimeSpan(hh, mm, 0);
            return DateTimeOffset.FromUnixTimeSeconds(s).ToOffset(zone[0] == '-' ? -offset : offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static bool IsObjectId(string s) =>
        s.Length is 40 or 64 && s.All(char.IsAsciiHexDigitLower);

    private sealed class Repo(string gitDir, string commonDir)
    {
        private Dictionary<string, string>? _packed;

        public GitInfo Read(string worktree)
        {
            var head = ReadText(gitDir, "HEAD", MaxSmallFile)?.Trim();
            string? branch = null;
            string? headId = null;
            if (head is not null && head.StartsWith("ref:", StringComparison.Ordinal))
            {
                var target = head[4..].Trim();
                if (target.StartsWith("refs/heads/", StringComparison.Ordinal))
                {
                    branch = target["refs/heads/".Length..];
                }

                headId = ResolveRef(target);
            }
            else if (head is not null && IsObjectId(head))
            {
                headId = head;
            }

            var config = ReadConfig();
            string? Get(string key) => config.LastOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

            // Remote URL: origin, else the first remote with a url.
            var url = Get("remote.origin.url") ?? config.FirstOrDefault(e => e.Section == "remote" && e.Name == "url" && e.Value is not null)?.Value;

            // Upstream of the checked-out branch: branch.<b>.remote + branch.<b>.merge, default fetch refspec.
            var hasUpstream = false;
            var upstreamGone = false;
            string? upstreamId = null;
            if (branch is not null && Get($"branch.{branch}.remote") is { Length: > 0 } remote && Get($"branch.{branch}.merge") is { Length: > 0 } merge)
            {
                hasUpstream = true;
                var trackingRef = remote == "."
                    ? merge
                    : merge.StartsWith("refs/heads/", StringComparison.Ordinal) ? $"refs/remotes/{remote}/{merge["refs/heads/".Length..]}" : null;
                upstreamId = trackingRef is null ? null : ResolveRef(trackingRef);
                upstreamGone = trackingRef is not null && upstreamId is null;
            }

            // Ahead/behind only when it can be proven from loose commits; otherwise "unknown" (UnpushedCommits = null).
            var ahead = 0;
            var behind = 0;
            var aheadBehindKnown = !hasUpstream || upstreamGone;
            int? unpushed = null;
            if (headId is null)
            {
                unpushed = branch is not null && ResolveRef($"refs/heads/{branch}") is null && LocalBranches().Count == 0 ? 0 : null; // unborn repository
            }
            else if (upstreamId is not null)
            {
                if (headId == upstreamId)
                {
                    aheadBehindKnown = true;
                    unpushed = AllLocalBranchesOnRemotes() ? 0 : null;
                }
                else if (Distance(headId, upstreamId) is { } a)
                {
                    ahead = a;
                    aheadBehindKnown = true;
                    unpushed = AllLocalBranchesOnRemotes(except: branch) ? a : null;
                }
                else if (Distance(upstreamId, headId) is { } b)
                {
                    behind = b;
                    aheadBehindKnown = true;
                    unpushed = AllLocalBranchesOnRemotes() ? 0 : null;
                }
            }
            else if (AllLocalBranchesOnRemotes())
            {
                unpushed = 0;
            }

            var (lastAt, subject) = headId is null ? (null, null) : LastCommit(headId);
            return new GitInfo
            {
                RemoteUrl = GitUrls.Sanitize(url?.Trim()),
                Branch = branch,
                HasUpstream = hasUpstream,
                UpstreamGone = upstreamGone,
                Ahead = ahead,
                Behind = behind,
                Stashes = CountStashes(),
                LastCommitAt = lastAt,
                LastCommitSubject = subject,
                HasCommits = headId is not null,
                AheadBehindUnknown = !aheadBehindKnown,
                UnpushedCommits = unpushed,
                LinkedWorktrees = LinkedWorktrees(worktree),
                StatusUnknown = true,
            };
        }

        private List<GitConfigEntry> ReadConfig()
        {
            var text = ReadText(commonDir, "config", MaxConfigBytes);
            GitConfigParser.TryParse(text, strict: false, out var entries, out _);
            return entries;
        }

        /// <summary>Follows symbolic refs (at most 5 hops) to an object id; loose refs first, then packed-refs.</summary>
        public string? ResolveRef(string name)
        {
            for (var hop = 0; hop < 5; hop++)
            {
                if (!IsSafeRefName(name))
                {
                    return null;
                }

                var dir = name.StartsWith("refs/", StringComparison.Ordinal) && !name.StartsWith("refs/bisect/", StringComparison.Ordinal)
                    && !name.StartsWith("refs/worktree/", StringComparison.Ordinal) ? commonDir : gitDir;
                var loose = ReadText(dir, name, MaxSmallFile)?.Trim();
                if (loose is null && !ReferenceEquals(dir, commonDir))
                {
                    loose = ReadText(commonDir, name, MaxSmallFile)?.Trim();
                }

                if (loose is not null)
                {
                    if (loose.StartsWith("ref:", StringComparison.Ordinal))
                    {
                        name = loose[4..].Trim();
                        continue;
                    }

                    return IsObjectId(loose) ? loose : null;
                }

                return Packed().TryGetValue(name, out var id) ? id : null;
            }

            return null;
        }

        private static bool IsSafeRefName(string name) =>
            name.Length is > 0 and < 1024 && !name.Contains("..", StringComparison.Ordinal) && !name.Contains('\\', StringComparison.Ordinal)
            && !name.Contains(':', StringComparison.Ordinal) && !name.StartsWith('/') && name.Split('/').All(s => s.Length > 0 && s != ".");

        private Dictionary<string, string> Packed()
        {
            if (_packed is not null)
            {
                return _packed;
            }

            _packed = new Dictionary<string, string>(StringComparer.Ordinal);
            var text = ReadText(commonDir, "packed-refs", MaxPackedRefs);
            if (text is null)
            {
                return _packed;
            }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] is '#' or '^')
                {
                    continue;
                }

                var sp = line.IndexOf(' ', StringComparison.Ordinal);
                if (sp > 0 && IsObjectId(line[..sp]))
                {
                    _packed[line[(sp + 1)..]] = line[..sp];
                }
            }

            return _packed;
        }

        /// <summary>(name → id) of every ref under <paramref name="prefix"/>, loose and packed.</summary>
        private Dictionary<string, string> RefsUnder(string prefix)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, id) in Packed())
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    result[name] = id;
                }
            }

            var root = Path.Combine(commonDir, prefix.Replace('/', '\\'));
            if (Directory.Exists(root) && GitRepoGuard.FindLinkBelow(commonDir, root) is null)
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true, MaxRecursionDepth = 16 };
                foreach (var file in Directory.EnumerateFiles(root, "*", options).Take(10_000))
                {
                    var name = prefix + Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (ResolveRef(name) is { } id)
                    {
                        result[name] = id;
                    }
                }
            }

            return result;
        }

        private Dictionary<string, string> LocalBranches() => RefsUnder("refs/heads/");

        /// <summary>True when every local branch (optionally except one) points at a commit some remote-tracking ref has.</summary>
        private bool AllLocalBranchesOnRemotes(string? except = null)
        {
            var remoteIds = RefsUnder("refs/remotes/").Values.ToHashSet(StringComparer.Ordinal);
            return LocalBranches().All(kv => (except is not null && kv.Key == $"refs/heads/{except}") || remoteIds.Contains(kv.Value));
        }

        /// <summary>
        /// Number of commits between <paramref name="from"/> and its ancestor <paramref name="to"/>, walking single-parent
        /// loose commits only. Null when that cannot be proven within <see cref="MaxAncestryWalk"/> commits (packed object,
        /// merge, root reached).
        /// </summary>
        private int? Distance(string from, string to)
        {
            var current = from;
            for (var steps = 0; steps <= MaxAncestryWalk; steps++)
            {
                if (current == to)
                {
                    return steps;
                }

                var commit = ReadCommit(current);
                if (commit is null || commit.Parents.Count != 1)
                {
                    return null; // packed object, root commit or merge: not provable cheaply
                }

                current = commit.Parents[0];
            }

            return null;
        }

        private (DateTimeOffset? At, string? Subject) LastCommit(string headId)
        {
            if (ReadCommit(headId) is { } commit)
            {
                return (commit.CommittedAt, commit.Subject);
            }

            // Packed object. The newest HEAD reflog entry records when HEAD last moved (clone, checkout, pull, reset...), which
            // is not the commit time; it is used only when it is the record of this very commit being made here ("commit",
            // "commit (initial)", "commit (amend)", "commit (merge)" moving HEAD to headId). Otherwise the time is unknown.
            var last = LastLine(Path.Combine(gitDir, "logs", "HEAD"));
            if (last is null)
            {
                return (null, null);
            }

            var tab = last.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0)
            {
                return (null, null);
            }

            var meta = last[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var message = last[(tab + 1)..];
            if (meta.Length < 4 || !string.Equals(meta[1], headId, StringComparison.Ordinal))
            {
                return (null, null);
            }

            foreach (var prefix in new[] { "commit: ", "commit (initial): ", "commit (amend): ", "commit (merge): " })
            {
                if (message.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var subject = message[prefix.Length..];
                    return (ParseGitTime(meta[^2], meta[^1]), subject.Length == 0 ? null : subject);
                }
            }

            return (null, null);
        }

        private sealed record Commit(IReadOnlyList<string> Parents, DateTimeOffset? CommittedAt, string? Subject);

        /// <summary>Reads a loose commit object (zlib); null when it is packed, missing, not a commit or malformed.</summary>
        private Commit? ReadCommit(string id)
        {
            if (!IsObjectId(id))
            {
                return null;
            }

            var path = Path.Combine(commonDir, "objects", id[..2], id[2..]);
            if (!SafeToRead(commonDir, path, out var length) || length > MaxCommitBytes)
            {
                return null;
            }

            byte[] data;
            using (var file = File.OpenRead(path))
            using (var z = new ZLibStream(file, CompressionMode.Decompress))
            {
                var buffer = new byte[MaxCommitBytes + 64];
                var total = 0;
                int read;
                while (total < buffer.Length && (read = z.Read(buffer, total, buffer.Length - total)) > 0)
                {
                    total += read;
                }

                data = buffer[..total];
            }

            var nul = Array.IndexOf(data, (byte)0);
            if (nul < 0 || !Encoding.ASCII.GetString(data, 0, nul).StartsWith("commit ", StringComparison.Ordinal))
            {
                return null;
            }

            var text = Encoding.UTF8.GetString(data, nul + 1, data.Length - nul - 1);
            var parents = new List<string>();
            DateTimeOffset? committed = null;
            var lines = text.Split('\n');
            var i = 0;
            for (; i < lines.Length && lines[i].Length > 0; i++)
            {
                var line = lines[i];
                if (line.StartsWith("parent ", StringComparison.Ordinal))
                {
                    parents.Add(line["parent ".Length..].Trim());
                }
                else if (line.StartsWith("committer ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ');
                    committed = parts.Length >= 3 ? ParseGitTime(parts[^2], parts[^1]) : null;
                }

                // other headers (author, gpgsig with its continuation lines, encoding...) are irrelevant
            }

            var subject = i + 1 < lines.Length ? lines[i + 1].TrimEnd('\r') : null;
            return new Commit(parents, committed, string.IsNullOrEmpty(subject) ? null : subject);
        }

        private int CountStashes()
        {
            if (ResolveRef("refs/stash") is null)
            {
                return 0;
            }

            var path = Path.Combine(commonDir, "logs", "refs", "stash");
            if (!SafeToRead(commonDir, path, out var length) || length > MaxPackedRefs)
            {
                return 1;
            }

            var count = File.ReadLines(path).Count(l => l.Length > 0);
            return Math.Max(1, count);
        }

        /// <summary>Linked worktrees registered in the main repository (paths only: nothing at those paths is touched).</summary>
        private List<string> LinkedWorktrees(string worktree)
        {
            if (!string.Equals(gitDir, commonDir, StringComparison.OrdinalIgnoreCase))
            {
                return []; // this is itself a linked worktree
            }

            var root = Path.Combine(commonDir, "worktrees");
            if (!Directory.Exists(root) || GitRepoGuard.FindLinkBelow(commonDir, root) is not null)
            {
                return [];
            }

            var result = new List<string>();
            foreach (var dir in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }).Take(100))
            {
                var pointer = ReadText(dir, "gitdir", MaxSmallFile)?.Split('\n')[0].Trim();
                if (string.IsNullOrEmpty(pointer))
                {
                    continue;
                }

                // "<worktree>/.git" as written by git; reported as text, never opened.
                var p = pointer.Replace('/', '\\');
                if (p.EndsWith(@"\.git", StringComparison.OrdinalIgnoreCase))
                {
                    p = p[..^5];
                }

                if (!string.Equals(p, worktree, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(p);
                }
            }

            return result;
        }

        /// <summary>Reads <paramref name="relative"/> under <paramref name="baseDir"/> when no link sits on the way and it is small.</summary>
        private static string? ReadText(string baseDir, string relative, long maxBytes)
        {
            var path = Path.GetFullPath(Path.Combine(baseDir, relative.Replace('/', '\\')));
            if (!Safety.PathGuard.IsSameOrUnder(path, baseDir) || !SafeToRead(baseDir, path, out var length) || length > maxBytes)
            {
                return null;
            }

            return File.ReadAllText(path);
        }

        private static bool SafeToRead(string baseDir, string path, out long length)
        {
            length = 0;
            var fi = new FileInfo(path);
            if (!fi.Exists || GitRepoGuard.FindLinkBelow(baseDir, path) is not null)
            {
                return false;
            }

            length = fi.Length;
            return true;
        }

        private static string? LastLine(string path)
        {
            var dir = Path.GetDirectoryName(Path.GetDirectoryName(path)!)!;
            if (!SafeToRead(dir, path, out var length) || length == 0)
            {
                return null;
            }

            using var fs = File.OpenRead(path);
            var take = (int)Math.Min(length, 16 * 1024);
            fs.Seek(-take, SeekOrigin.End);
            var buffer = new byte[take];
            fs.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer).TrimEnd('\n', '\r');
            var nl = text.LastIndexOf('\n');
            return nl >= 0 ? text[(nl + 1)..].TrimEnd('\r') : (take == length ? text : null);
        }
    }
}
