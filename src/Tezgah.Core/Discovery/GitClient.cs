using System.Globalization;
using Microsoft.Extensions.Logging;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.Core.Discovery;

/// <summary>
/// <see cref="IGitClient"/> backed by the git CLI. Never prompts for credentials (GIT_TERMINAL_PROMPT=0, GCM non-interactive)
/// and never bypasses <c>safe.directory</c>: repositories git refuses as "dubious ownership" yield <c>null</c>.
/// </summary>
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
    };

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

        if (fetch)
        {
            var f = await RunGitAsync(repoPath, NetworkTimeout, ct, "fetch", "--quiet", "--prune").ConfigureAwait(false);
            if (!f.Success)
            {
                logger.LogInformation("git fetch failed for {Path}: {Error}", repoPath, FirstLine(f.StdErr));
            }
        }

        var status = await RunGitAsync(repoPath, LocalTimeout, ct, "status", "--porcelain=v2", "--branch", "--show-stash", "--untracked-files=normal").ConfigureAwait(false);
        if (!status.Success && status.StdErr.Contains("unknown option", StringComparison.OrdinalIgnoreCase))
        {
            // very old git without --show-stash
            status = await RunGitAsync(repoPath, LocalTimeout, ct, "status", "--porcelain=v2", "--branch", "--untracked-files=normal").ConfigureAwait(false);
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
            var stash = await RunGitAsync(repoPath, LocalTimeout, ct, "stash", "list").ConfigureAwait(false);
            stashes = stash.Success ? stash.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 0;
        }

        DateTimeOffset? lastCommitAt = null;
        string? subject = null;
        if (!parsed.IsInitialCommit)
        {
            var log = await RunGitAsync(repoPath, LocalTimeout, ct, "log", "-1", "--format=%cI%x00%s").ConfigureAwait(false);
            if (log.Success)
            {
                (lastCommitAt, subject) = ParseLog(log.StdOut);
            }
        }

        var remote = await RunGitAsync(repoPath, LocalTimeout, ct, "remote", "get-url", "origin").ConfigureAwait(false);
        string? remoteUrl = remote.Success ? GitUrls.Sanitize(remote.StdOut.Trim()) : null;
        if (remoteUrl is null)
        {
            // no "origin": take the first remote, if any
            var remotes = await RunGitAsync(repoPath, LocalTimeout, ct, "remote").ConfigureAwait(false);
            var first = remotes.Success ? remotes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() : null;
            if (first is not null && !first.StartsWith('-'))
            {
                var url = await RunGitAsync(repoPath, LocalTimeout, ct, "remote", "get-url", "--", first).ConfigureAwait(false);
                remoteUrl = url.Success ? GitUrls.Sanitize(url.StdOut.Trim()) : null;
            }
        }

        return new GitInfo
        {
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
        return await RunGitAsync(repoPath, PullTimeout, ct, "pull", "--ff-only").ConfigureAwait(false);
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

    internal static bool IsDubiousOwnership(string stderr) =>
        stderr.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("safe.directory", StringComparison.OrdinalIgnoreCase);

    private static Task<CommandResult> RunGitAsync(string repoPath, TimeSpan timeout, CancellationToken ct, params string[] args)
    {
        var all = new List<string>(args.Length + 2) { "-C", repoPath };
        all.AddRange(args);
        return Cli.RunAsync("git", all, repoPath, timeout, ct, GitEnvironment);
    }

    private static string FirstLine(string s) => s.Split('\n', 2)[0].Trim();
}
