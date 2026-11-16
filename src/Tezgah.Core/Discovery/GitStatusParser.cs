using System.Globalization;

namespace Tezgah.Core.Discovery;

/// <summary>Parsed output of <c>git status --porcelain=v2 --branch</c>.</summary>
internal sealed record GitStatus(string? Branch, string? Upstream, int Ahead, int Behind, int Modified, int Untracked, int? Stashes, bool IsInitialCommit)
{
    /// <summary>
    /// git prints <c>branch.upstream</c> but no <c>branch.ab</c> line when the upstream ref no longer exists ("gone").
    /// </summary>
    public bool UpstreamGone { get; init; }
}

/// <summary>Robust parser for <c>git status --porcelain=v2 --branch [--show-stash]</c>.</summary>
internal static class GitStatusParser
{
    public static GitStatus Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string? branch = null;
        string? upstream = null;
        int ahead = 0, behind = 0, modified = 0, untracked = 0;
        int? stashes = null;
        var initial = false;
        var sawAheadBehind = false;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var header = line[2..];
                if (header.StartsWith("branch.oid ", StringComparison.Ordinal))
                {
                    initial = header.EndsWith("(initial)", StringComparison.Ordinal);
                }
                else if (header.StartsWith("branch.head ", StringComparison.Ordinal))
                {
                    var head = header["branch.head ".Length..].Trim();
                    branch = head == "(detached)" ? null : head;
                }
                else if (header.StartsWith("branch.upstream ", StringComparison.Ordinal))
                {
                    upstream = header["branch.upstream ".Length..].Trim();
                }
                else if (header.StartsWith("branch.ab ", StringComparison.Ordinal))
                {
                    sawAheadBehind = true;
                    foreach (var part in header["branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (part.Length > 1 && int.TryParse(part.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        {
                            if (part[0] == '+')
                            {
                                ahead = n;
                            }
                            else if (part[0] == '-')
                            {
                                behind = n;
                            }
                        }
                    }
                }
                else if (header.StartsWith("stash ", StringComparison.Ordinal) &&
                         int.TryParse(header.AsSpan("stash ".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                {
                    stashes = s;
                }

                continue;
            }

            switch (line[0])
            {
                case '1' when line.Length > 1 && line[1] == ' ':
                case '2' when line.Length > 1 && line[1] == ' ':
                case 'u' when line.Length > 1 && line[1] == ' ':
                    modified++;
                    break;
                case '?' when line.Length > 1 && line[1] == ' ':
                    untracked++;
                    break;
                default:
                    break; // '!' ignored entries and anything unknown
            }
        }

        return new GitStatus(branch, upstream, ahead, behind, modified, untracked, stashes, initial) { UpstreamGone = upstream is not null && !sawAheadBehind };
    }
}
