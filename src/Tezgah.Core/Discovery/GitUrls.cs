using System.Text.RegularExpressions;

namespace Tezgah.Core.Discovery;

/// <summary>Helpers for git remote URLs.</summary>
internal static partial class GitUrls
{
    /// <summary>Removes credentials (<c>https://user:token@host/...</c>) so they never reach the catalog file or the UI.</summary>
    public static string? Sanitize(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote))
        {
            return null;
        }

        var r = remote.Trim();
        if ((r.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || r.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) &&
            Uri.TryCreate(r, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0)
        {
            var b = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
            return b.Uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
        }

        return r;
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

    /// <summary>scp-like syntax: <c>[user@]host:path</c> (no scheme, host without slashes, path not starting with a drive letter backslash).</summary>
    [GeneratedRegex(@"^(?:[A-Za-z0-9._\-]+@)?(?<host>[A-Za-z0-9.\-]+\.[A-Za-z]{2,}|[A-Za-z0-9\-]{2,}):(?!//)(?<path>[^\\\s]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpLike();
}
