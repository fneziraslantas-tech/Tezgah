using System.Buffers;
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

    /// <summary>
    /// Only the stash count and the linked worktrees, which the git CLI path takes from this reader: no ref is resolved for
    /// ahead/behind and no commit is read (git already computed those). Null where <see cref="Read"/> would be null.
    /// </summary>
    public static (int Stashes, IReadOnlyList<string> LinkedWorktrees)? ReadLocalState(string worktree)
    {
        if (!GitRepoGuard.TryLocateGitDirs(worktree, out var gitDir, out var commonDir, out _))
        {
            return null;
        }

        try
        {
            var repo = new Repo(gitDir!, commonDir!);
            return (repo.CountStashes(), repo.LinkedWorktrees(Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Why a submodule's git directory may hold work that exists nowhere else.</summary>
    internal enum ModuleProblem
    {
        None,
        Unreadable,
        NoRemote,
        Stash,
        UnpushedBranches,
        UnpushedHead,
    }

    /// <summary>A submodule git directory under <c>&lt;commonDir&gt;/modules</c> and what its refs show.</summary>
    internal sealed record ModuleState(string Name, string GitDir, ModuleProblem Problem, IReadOnlyList<string> Branches);

    private const int MaxModules = 100;
    private const int MaxModuleWalkDirectories = 2_000;

    /// <summary>
    /// Submodule git directories stored in the repository's common directory (<c>modules/&lt;name&gt;</c>, nested ones as
    /// <c>modules/a/modules/b</c>). After <c>git submodule deinit</c> or <c>git rm</c> the working tree is gone and this is
    /// the only copy of the submodule repository, commits never pushed included. Each one (except those in
    /// <paramref name="skipGitDirs"/>, whose checked-out working trees are inspected on their own) is checked from its refs
    /// alone, without running git: every local branch and HEAD must be on a remote-tracking ref (or provably behind one),
    /// there is a remote and no stash. Null when the modules folder cannot be fully enumerated (link, unreadable folder,
    /// too many entries): the caller fails closed.
    /// </summary>
    internal static IReadOnlyList<ModuleState>? ReadSubmoduleGitDirs(string commonDir, IReadOnlySet<string> skipGitDirs)
    {
        try
        {
            var root = Path.Combine(commonDir, "modules");
            if (File.Exists(root))
            {
                return null;
            }

            if (!Directory.Exists(root))
            {
                return [];
            }

            if (GitRepoGuard.FindLinkBelow(commonDir, root) is not null)
            {
                return null;
            }

            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
            var result = new List<ModuleState>();
            var found = 0;
            var visited = 0;
            var stack = new Stack<(string Dir, string Prefix, int Depth)>();
            stack.Push((root, string.Empty, 0));
            while (stack.Count > 0)
            {
                var (dir, prefix, depth) = stack.Pop();
                foreach (var child in new DirectoryInfo(dir).EnumerateDirectories("*", options))
                {
                    if (++visited > MaxModuleWalkDirectories || depth > 16)
                    {
                        return null;
                    }

                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0 && Safety.FileRemover.IsLinkLike(child))
                    {
                        return null; // never followed; the deletion would remove only the link, but its contents cannot be vouched for
                    }

                    var name = prefix + child.Name;
                    if (!File.Exists(Path.Combine(child.FullName, "HEAD")))
                    {
                        stack.Push((child.FullName, name + "/", depth + 1)); // a name with slashes: modules/libs/foo
                        continue;
                    }

                    if (++found > MaxModules)
                    {
                        return null;
                    }

                    var full = Path.TrimEndingDirectorySeparator(child.FullName);
                    if (!skipGitDirs.Contains(full))
                    {
                        result.Add(CheckModule(name, full));
                    }

                    var inner = Path.Combine(full, "modules");
                    if (Directory.Exists(inner))
                    {
                        stack.Push((inner, name + "/", depth + 1));
                    }
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static ModuleState CheckModule(string name, string gitDir)
    {
        try
        {
            var (problem, branches) = new Repo(gitDir, gitDir).PushState();
            return new ModuleState(name, gitDir, problem, branches);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException or FormatException)
        {
            return new ModuleState(name, gitDir, ModuleProblem.Unreadable, []);
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

    /// <summary>First decompression buffer for a commit; commit objects are usually well under 1 KB.</summary>
    private const int InitialCommitBuffer = 4 * 1024;

    /// <summary>Parsed commits kept per <see cref="Repo"/>, so both Distance directions and OnRemote's walks share them.</summary>
    private const int MaxCachedCommits = 4096;

    /// <summary>
    /// Length of the prefix of an inflated commit object that holds the object header, the commit headers, the blank line
    /// and the first message line (through its newline); -1 while more data is needed.
    /// </summary>
    internal static int CommitPrefixLength(ReadOnlySpan<byte> data)
    {
        var nul = data.IndexOf((byte)0);
        if (nul < 0)
        {
            return -1;
        }

        var body = data[(nul + 1)..];
        int blank;
        if (body.Length > 0 && body[0] == (byte)'\n')
        {
            blank = 0;
        }
        else
        {
            var b = body.IndexOf("\n\n"u8);
            if (b < 0)
            {
                return -1;
            }

            blank = b + 1;
        }

        var subjectStart = blank + 1;
        var nl = body[subjectStart..].IndexOf((byte)'\n');
        return nl < 0 ? -1 : nul + 1 + subjectStart + nl + 1;
    }

    private sealed class Repo(string gitDir, string commonDir)
    {
        private readonly Dictionary<string, Commit?> _commits = new(StringComparer.Ordinal);
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

        /// <summary>Whether everything this git directory's refs point at is on a remote-tracking ref (see <see cref="ReadSubmoduleGitDirs"/>).</summary>
        public (ModuleProblem Problem, IReadOnlyList<string> Branches) PushState()
        {
            if (!ReadConfig().Any(e => e.Section == "remote" && e.Name == "url" && !string.IsNullOrWhiteSpace(e.Value)))
            {
                return (ModuleProblem.NoRemote, []);
            }

            if (ResolveRef("refs/stash") is not null)
            {
                return (ModuleProblem.Stash, []);
            }

            var remoteIds = RefsUnder("refs/remotes/").Values.ToHashSet(StringComparer.Ordinal);
            var unpushed = LocalBranches()
                .Where(kv => !OnRemote(kv.Value, remoteIds))
                .Select(kv => kv.Key["refs/heads/".Length..])
                .Order(StringComparer.Ordinal)
                .ToList();
            if (unpushed.Count > 0)
            {
                return (ModuleProblem.UnpushedBranches, unpushed.Take(5).ToList());
            }

            var head = ReadText(gitDir, "HEAD", MaxSmallFile)?.Trim();
            if (head is null)
            {
                return (ModuleProblem.Unreadable, []);
            }

            var headId = head.StartsWith("ref:", StringComparison.Ordinal) ? ResolveRef(head[4..].Trim()) : IsObjectId(head) ? head : null;
            if (headId is null && !head.StartsWith("ref:", StringComparison.Ordinal))
            {
                return (ModuleProblem.Unreadable, []);
            }

            return headId is not null && !OnRemote(headId, remoteIds) ? (ModuleProblem.UnpushedHead, []) : (ModuleProblem.None, []);
        }

        /// <summary><paramref name="id"/> is a remote-tracking tip or provably (loose commits) an ancestor of one.</summary>
        private bool OnRemote(string id, HashSet<string> remoteIds) =>
            remoteIds.Contains(id) || remoteIds.Take(20).Any(r => Distance(r, id) is not null);

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

        /// <summary>Reads a loose commit object (zlib); null when it is packed, missing, not a commit or malformed. Memoized.</summary>
        private Commit? ReadCommit(string id)
        {
            if (_commits.TryGetValue(id, out var cached))
            {
                return cached;
            }

            var commit = LoadCommit(id);
            if (_commits.Count < MaxCachedCommits)
            {
                _commits[id] = commit;
            }

            return commit;
        }

        /// <summary>
        /// Inflates only what the parents, committer and subject need (see <see cref="CommitPrefixLength"/>), into a pooled
        /// buffer that starts small and grows up to <see cref="MaxCommitBytes"/>.
        /// </summary>
        private Commit? LoadCommit(string id)
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

            const int cap = MaxCommitBytes + 64;
            var buffer = ArrayPool<byte>.Shared.Rent(InitialCommitBuffer);
            try
            {
                var total = 0;
                using (var file = File.OpenRead(path))
                using (var z = new ZLibStream(file, CompressionMode.Decompress))
                {
                    while (true)
                    {
                        var limit = Math.Min(buffer.Length, cap);
                        if (total == limit)
                        {
                            if (limit == cap)
                            {
                                break;
                            }

                            var bigger = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, cap));
                            buffer.AsSpan(0, total).CopyTo(bigger);
                            ArrayPool<byte>.Shared.Return(buffer);
                            buffer = bigger;
                            continue;
                        }

                        var read = z.Read(buffer, total, limit - total);
                        if (read == 0)
                        {
                            break;
                        }

                        total += read;
                        if (CommitPrefixLength(buffer.AsSpan(0, total)) >= 0)
                        {
                            break;
                        }
                    }
                }

                return ParseCommit(buffer.AsSpan(0, total));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static Commit? ParseCommit(ReadOnlySpan<byte> data)
        {
            var nul = data.IndexOf((byte)0);
            if (nul < 0 || !data[..nul].StartsWith("commit "u8))
            {
                return null;
            }

            var needed = CommitPrefixLength(data);
            var text = Encoding.UTF8.GetString(needed >= 0 ? data[(nul + 1)..needed] : data[(nul + 1)..]);
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

        public int CountStashes()
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
        public List<string> LinkedWorktrees(string worktree)
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
            var buffer = ArrayPool<byte>.Shared.Rent(take);
            string text;
            try
            {
                fs.ReadExactly(buffer, 0, take);
                text = Encoding.UTF8.GetString(buffer, 0, take).TrimEnd('\n', '\r');
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var nl = text.LastIndexOf('\n');
            return nl >= 0 ? text[(nl + 1)..].TrimEnd('\r') : (take == length ? text : null);
        }
    }
}
