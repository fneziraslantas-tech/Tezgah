using System.Text.RegularExpressions;

namespace Tezgah.Core.Discovery;

/// <summary>Helpers for git remote URLs.</summary>
internal static partial class GitUrls
{
    /// <summary>
    /// Removes credentials so they never reach the catalog file or the UI, for every <c>scheme://</c> URL and without
    /// relying on <see cref="Uri"/> (which rejects e.g. an unescaped '@' that git and curl accept):
    /// everything up to the LAST '@' of the authority is dropped (ssh-like schemes keep a plain user name such as
    /// <c>git@</c>, never a password), and query/fragment (<c>?private_token=...</c>) are removed.
    /// scp-like remotes (<c>git@host:path</c>) cannot carry a password and are returned unchanged.
    /// </summary>
    public static string? Sanitize(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote))
        {
            return null;
        }

        var r = remote.Trim();
        var m = SchemeUrl().Match(r);
        if (!m.Success)
        {
            return r;
        }

        var scheme = m.Groups["s"].Value;
        var authority = m.Groups["auth"].Value;
        var rest = m.Groups["rest"].Value;
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            var userInfo = authority[..at];
            var host = authority[(at + 1)..];
            var isWeb = scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase) || scheme.StartsWith("ftp", StringComparison.OrdinalIgnoreCase);
            var user = userInfo.Split(':')[0];
            authority = !isWeb && user.Length > 0 && !user.Contains('@', StringComparison.Ordinal) ? $"{user}@{host}" : host;
        }

        var cut = rest.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            rest = rest[..cut];
        }

        return scheme + authority + rest;
    }

    /// <summary>
    /// Converts a remote to a browsable https URL:
    /// <c>git@github.com:x/y.git</c>, <c>ssh://git@github.com/x/y.git</c> and <c>https://github.com/x/y.git</c> all become
    /// <c>https://github.com/x/y</c>. Returns null for local/unknown remotes.
    /// </summary>
    public static string? ToWebUrl(string? remote)
    {
        var r = Sanitize(remote);
        if (r is null)
        {
            return null;
        }

        string host;
        string path;
        var scp = ScpLike().Match(r);
        if (scp.Success)
        {
            host = scp.Groups["host"].Value;
            path = scp.Groups["path"].Value;
        }
        else if (Uri.TryCreate(r, UriKind.Absolute, out var uri) &&
                 (uri.Scheme is "https" or "http" or "ssh" or "git") && !string.IsNullOrEmpty(uri.Host))
        {
            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            return null;
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        if (path.Length == 0 || host.Length == 0)
        {
            return null;
        }

        return $"https://{host.ToLowerInvariant()}/{path}";
    }

    [GeneratedRegex(@"^(?<s>[A-Za-z][A-Za-z0-9+.\-]*://)(?<auth>[^/?#]*)(?<rest>.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SchemeUrl();

    /// <summary>scp-like syntax: <c>[user@]host:path</c> (no scheme, host without slashes, path not starting with a drive letter backslash).</summary>
    [GeneratedRegex(@"^(?:[A-Za-z0-9._\-]+@)?(?<host>[A-Za-z0-9.\-]+\.[A-Za-z]{2,}|[A-Za-z0-9\-]{2,}):(?!//)(?<path>[^\\\s]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpLike();
}
