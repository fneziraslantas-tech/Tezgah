using System.Globalization;
using Microsoft.Extensions.Logging;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.Core.Discovery;

/// <summary>
/// <see cref="IGitClient"/> backed by the git CLI. Never prompts for credentials (GIT_TERMINAL_PROMPT=0, GCM non-interactive)
/// and never bypasses <c>safe.directory</c>: repositories git refuses as "dubious ownership" yield <c>null</c>.
/// </summary>
/// <remarks>
/// Scanned repositories are untrusted input: their own <c>.git/config</c> can name programs git runs during read-only
/// commands (<c>core.fsmonitor</c>, filter drivers, hooks, external diff...). Every invocation therefore
/// <list type="bullet">
/// <item>passes <see cref="GitRepoGuard"/> first (no git process for repositories whose git data is not on a local drive);</item>
/// <item>carries command-scope config overrides through <c>GIT_CONFIG_COUNT</c>/<c>GIT_CONFIG_KEY_n</c>/<c>GIT_CONFIG_VALUE_n</c>
/// (same precedence as <c>-c</c>, beats repository config and includes, inherited by child git processes);</item>
/// <item>for read-only calls also neutralises every filter driver defined in the repository config;</item>
/// <item>runs with a neutral working directory and <c>GIT_CEILING_DIRECTORIES</c> so nothing is resolved from the repository folder.</item>
/// </list>
/// Network operations (fetch/pull) honour repository settings such as <c>core.sshCommand</c> and must only be
/// requested for items the user trusted (see <c>CatalogService.RefreshItemAsync</c> and the UI trust prompt).
/// </remarks>
internal sealed class GitClient(ILogger<GitClient> logger) : IGitClient
{
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PullTimeout = TimeSpan.FromMinutes(5);

    internal static readonly IReadOnlyDictionary<string, string> GitEnvironment = new Dictionary<string, string>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
        ["GIT_ASKPASS"] = string.Empty,
        ["SSH_ASKPASS"] = string.Empty,
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["LC_ALL"] = "C",
        ["LANG"] = "C",
        ["GIT_NO_REPLACE_OBJECTS"] = "1",
        ["GIT_PAGER"] = "cat",
    };

    /// <summary>Config forced on every git call (key, value). Values here win over the repository's own config.</summary>
    internal static readonly IReadOnlyList<KeyValuePair<string, string>> HardeningConfig =
    [
        new("core.fsmonitor", "false"),
        new("core.untrackedCache", "false"),
        new("core.hooksPath", "NUL"),
        new("core.pager", "cat"),
        new("core.askPass", string.Empty),
        new("diff.external", string.Empty),
        new("uploadpack.packObjectsHook", string.Empty),
        new("protocol.ext.allow", "never"),
        new("protocol.fd.allow", "never"),
        new("protocol.file.allow", "user"),
        new("submodule.recurse", "false"),
        new("fetch.recurseSubmodules", "false"),
        new("safe.bareRepository", "explicit"),
    ];

    /// <summary>Neutral process working directory: the repository is selected with <c>-C</c>, never via the CWD.</summary>
    private static string NeutralDirectory => Environment.SystemDirectory;

    private bool? _available;

    public Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        _available ??= Cli.ResolveExecutable("git") is not null;
        return Task.FromResult(_available.Value);
    }

    public async Task<GitInfo?> GetInfoAsync(string repoPath, bool fetch, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoPath);
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        if (!GitRepoGuard.TryResolveLocalGitDir(repoPath, out _, out var refused))
        {
            logger.LogWarning("git details skipped for {Path}: {Reason}", repoPath, refused);
            return null;
        }

        var filters = await ReadFilterDriversAsync(repoPath, ct).ConfigureAwait(false);
        if (filters is null)
        {
            logger.LogInformation("git config could not be read for {Path}; git details skipped.", repoPath);
            return null;
        }

        Task<CommandResult> RunGitAsync(TimeSpan timeout, CancellationToken token, params string[] args) =>
            RunHardenedAsync(repoPath, timeout, filters, token, args);

        if (fetch)
        {
            var f = await RunHardenedAsync(repoPath, NetworkTimeout, [], ct, "fetch", "--quiet", "--prune").ConfigureAwait(false);
            if (!f.Success)
            {
                logger.LogInformation("git fetch failed for {Path}: {Error}", repoPath, FirstLine(f.StdErr));
            }
        }

        var status = await RunGitAsync(LocalTimeout, ct, "status", "--porcelain=v2", "--branch", "--show-stash", "--untracked-files=normal").ConfigureAwait(false);
        if (!status.Success && status.StdErr.Contains("unknown option", StringComparison.OrdinalIgnoreCase))
        {
            // very old git without --show-stash
            status = await RunGitAsync(LocalTimeout, ct, "status", "--porcelain=v2", "--branch", "--untracked-files=normal").ConfigureAwait(false);
        }

        if (!status.Success)
        {
            if (IsDubiousOwnership(status.StdErr))
            {
                logger.LogWarning("git refuses {Path} (dubious ownership / safe.directory); git details skipped.", repoPath);
            }
            else
            {
                logger.LogInformation("git status failed for {Path}: {Error}", repoPath, FirstLine(status.StdErr));
            }

            return null;
        }

        var parsed = GitStatusParser.Parse(status.StdOut);
        var stashes = parsed.Stashes;
        if (stashes is null)
        {
            var stash = await RunGitAsync(LocalTimeout, ct, "stash", "list").ConfigureAwait(false);
            stashes = stash.Success ? stash.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 0;
        }

        DateTimeOffset? lastCommitAt = null;
        string? subject = null;
        int? unpushed = 0;
        IReadOnlyList<string> unpushedBranches = [];
        if (!parsed.IsInitialCommit)
        {
            // Work on ANY local branch that no remote-tracking ref has (other branches, no upstream, gone upstream).
            var count = await RunGitAsync(LocalTimeout, ct, "rev-list", "--count", "--branches", "--not", "--remotes").ConfigureAwait(false);
            unpushed = count.Success && int.TryParse(count.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
            if (unpushed is not 0)
            {
                var refs = await RunGitAsync(LocalTimeout, ct, "for-each-ref", "--format=%(refname:short)%00%(upstream)%00%(upstream:track)", "refs/heads").ConfigureAwait(false);
                unpushedBranches = refs.Success ? ParseUnpushedBranches(refs.StdOut) : [];
            }

            var log = await RunGitAsync(LocalTimeout, ct, "log", "-1", "--format=%cI%x00%s").ConfigureAwait(false);
            if (log.Success)
            {
                (lastCommitAt, subject) = ParseLog(log.StdOut);
            }
        }

        var remote = await RunGitAsync(LocalTimeout, ct, "remote", "get-url", "origin").ConfigureAwait(false);
        string? remoteUrl = remote.Success ? GitUrls.Sanitize(remote.StdOut.Trim()) : null;
        if (remoteUrl is null)
        {
            // no "origin": take the first remote, if any
            var remotes = await RunGitAsync(LocalTimeout, ct, "remote").ConfigureAwait(false);
            var first = remotes.Success ? remotes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() : null;
            if (first is not null && !first.StartsWith('-'))
            {
                var url = await RunGitAsync(LocalTimeout, ct, "remote", "get-url", "--", first).ConfigureAwait(false);
                remoteUrl = url.Success ? GitUrls.Sanitize(url.StdOut.Trim()) : null;
            }
        }

        IReadOnlyList<string> worktrees = [];
        var wt = await RunGitAsync(LocalTimeout, ct, "worktree", "list", "--porcelain").ConfigureAwait(false);
        if (wt.Success)
        {
            worktrees = ParseLinkedWorktrees(wt.StdOut, repoPath);
        }

        return new GitInfo
        {
            UnpushedCommits = unpushed,
            UnpushedBranches = unpushedBranches,
            UpstreamGone = parsed.UpstreamGone,
            LinkedWorktrees = worktrees,
            RemoteUrl = remoteUrl,
            Branch = parsed.Branch,
            HasUpstream = parsed.Upstream is not null,
            Ahead = parsed.Ahead,
            Behind = parsed.Behind,
            ModifiedFiles = parsed.Modified,
            UntrackedFiles = parsed.Untracked,
            Stashes = stashes.Value,
            LastCommitAt = lastCommitAt,
            LastCommitSubject = subject,
        };
    }

    public async Task<CommandResult> PullAsync(string repoPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoPath);
        if (!GitRepoGuard.TryResolveLocalGitDir(repoPath, out _, out var refused))
        {
            return new CommandResult(-1, string.Empty, refused ?? "Refused: repository is not on a local drive.", false);
        }

        // Filters are not neutralised here: a pull of a trusted repository may legitimately need e.g. git-lfs smudge.
        return await RunHardenedAsync(repoPath, PullTimeout, [], ct, "pull", "--ff-only").ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetIgnoredPathsAsync(string repoPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoPath);
        if (!await IsAvailableAsync(ct).ConfigureAwait(false) || !GitRepoGuard.TryResolveLocalGitDir(repoPath, out _, out _))
        {
            return [];
        }

        var filters = await ReadFilterDriversAsync(repoPath, ct).ConfigureAwait(false);
        if (filters is null)
        {
            return [];
        }

        var r = await RunHardenedAsync(repoPath, LocalTimeout, filters, ct, "ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "-z").ConfigureAwait(false);
        return r.Success ? r.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).Take(5_000).ToList() : [];
    }

    internal static (DateTimeOffset? At, string? Subject) ParseLog(string output)
    {
        var line = output.TrimEnd('\r', '\n');
        var sep = line.IndexOf('\0', StringComparison.Ordinal);
        var datePart = sep >= 0 ? line[..sep] : line;
        var subject = sep >= 0 ? line[(sep + 1)..] : null;
        return DateTimeOffset.TryParse(datePart.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? (at, subject)
            : (null, subject);
    }

    /// <summary>
    /// Branches from <c>for-each-ref --format=%(refname:short)%00%(upstream)%00%(upstream:track)</c> that may hold work
    /// no remote has: no upstream, "[ahead N]" or "[gone]". At most 10 names.
    /// </summary>
    internal static IReadOnlyList<string> ParseUnpushedBranches(string output)
    {
        var result = new List<string>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.TrimEnd('\r').Split('\0');
            if (parts.Length < 3 || parts[0].Length == 0)
            {
                continue;
            }

            if (parts[1].Length == 0 || parts[2].Contains("ahead", StringComparison.Ordinal) || parts[2].Contains("gone", StringComparison.Ordinal))
            {
                result.Add(parts[0]);
                if (result.Count == 10)
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>Paths of linked worktrees from <c>git worktree list --porcelain</c> when <paramref name="repoPath"/> is the main worktree.</summary>
    internal static IReadOnlyList<string> ParseLinkedWorktrees(string output, string repoPath)
    {
        var paths = output.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(l => l["worktree ".Length..].Replace('/', '\\'))
            .ToList();
        if (paths.Count < 2)
        {
            return [];
        }

        var self = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath));
        return string.Equals(Path.TrimEndingDirectorySeparator(paths[0]), self, StringComparison.OrdinalIgnoreCase) ? paths.Skip(1).ToList() : [];
    }

    internal static bool IsDubiousOwnership(string stderr) =>
        stderr.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("safe.directory", StringComparison.OrdinalIgnoreCase);

    /// <summary>Names of the filter drivers configured for the repository (any scope), or null when config cannot be read.</summary>
    private static async Task<IReadOnlyList<string>?> ReadFilterDriversAsync(string repoPath, CancellationToken ct)
    {
        var r = await RunHardenedAsync(repoPath, LocalTimeout, [], ct, "config", "--null", "--name-only", "--get-regexp", @"^filter\.").ConfigureAwait(false);
        if (r.ExitCode == 1 && r.StdOut.Length == 0)
        {
            return []; // no filter.* keys at all
        }

        if (!r.Success)
        {
            return IsDubiousOwnership(r.StdErr) ? [] : null; // status reports dubious ownership itself
        }

        return FilterDriverNames(r.StdOut);
    }

    /// <summary>Extracts driver names from NUL-separated <c>filter.&lt;name&gt;.&lt;key&gt;</c> config keys.</summary>
    internal static IReadOnlyList<string> FilterDriverNames(string nulSeparatedKeys)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in nulSeparatedKeys.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var k = key.Trim('\r', '\n');
            if (!k.StartsWith("filter.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lastDot = k.LastIndexOf('.');
            if (lastDot > "filter.".Length)
            {
                names.Add(k["filter.".Length..lastDot]);
            }
        }

        return [.. names];
    }

    /// <summary>
    /// Environment for one git invocation: <see cref="GitEnvironment"/> plus the hardening config (and, for read-only
    /// calls, overrides that disable every listed filter driver) as GIT_CONFIG_COUNT pairs.
    /// </summary>
    internal static Dictionary<string, string> BuildEnvironment(string repoPath, IReadOnlyList<string> filterDrivers)
    {
        var env = new Dictionary<string, string>(GitEnvironment, StringComparer.OrdinalIgnoreCase);
        var config = new List<KeyValuePair<string, string>>(HardeningConfig);
        foreach (var name in filterDrivers)
        {
            config.Add(new($"filter.{name}.clean", string.Empty));
            config.Add(new($"filter.{name}.smudge", string.Empty));
            config.Add(new($"filter.{name}.process", string.Empty));
            config.Add(new($"filter.{name}.required", "false"));
        }

        env["GIT_CONFIG_COUNT"] = config.Count.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < config.Count; i++)
        {
            env[$"GIT_CONFIG_KEY_{i}"] = config[i].Key;
            env[$"GIT_CONFIG_VALUE_{i}"] = config[i].Value;
        }

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath)));
        if (!string.IsNullOrEmpty(parent))
        {
            env["GIT_CEILING_DIRECTORIES"] = parent;
        }

        return env;
    }

    /// <summary>Argument vector: global safety options, <c>-C repo</c>, then the subcommand.</summary>
    internal static List<string> BuildArguments(string repoPath, IReadOnlyList<string> args)
    {
        var all = new List<string>(args.Count + 4) { "--no-pager", "--no-optional-locks", "-C", repoPath };
        all.AddRange(args);
        return all;
    }

    private static Task<CommandResult> RunHardenedAsync(string repoPath, TimeSpan timeout, IReadOnlyList<string> filterDrivers, CancellationToken ct, params string[] args) =>
        Cli.RunAsync("git", BuildArguments(repoPath, args), NeutralDirectory, timeout, ct, BuildEnvironment(repoPath, filterDrivers));

    private static string FirstLine(string s) => s.Split('\n', 2)[0].Trim();
}
