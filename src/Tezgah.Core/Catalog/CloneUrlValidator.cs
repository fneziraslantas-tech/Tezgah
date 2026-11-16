using System.Text.RegularExpressions;

namespace Tezgah.Core.Catalog;

/// <summary>
/// Validates URLs passed to <c>git clone</c>. Only https and ssh remotes are accepted; option injection
/// (<c>--upload-pack=...</c>), transport helpers (<c>ext::</c>, <c>fd::</c>, <c>file::</c>), local paths and
/// <c>file://</c> URLs are refused.
/// </summary>
internal static partial class CloneUrlValidator
{
    public static bool IsValid(string? url, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            error = L.T("URL boş.", "URL is empty.");
            return false;
        }

        var u = url.Trim();
        if (u.StartsWith('-'))
        {
            error = L.T("URL '-' ile başlayamaz.", "URL must not start with '-'.");
            return false;
        }

        if (u.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            error = L.T("URL boşluk veya kontrol karakteri içeremez.", "URL must not contain whitespace or control characters.");
            return false;
        }

        if (u.Contains("::", StringComparison.Ordinal))
        {
            error = L.T("Git aktarım yardımcıları (ext::, file::...) desteklenmez.", "Git transport helpers (ext::, file::...) are not allowed.");
            return false;
        }

        if (u.Contains("--", StringComparison.Ordinal) && u.Contains("upload-pack", StringComparison.OrdinalIgnoreCase))
        {
            error = L.T("Geçersiz URL.", "Invalid URL.");
            return false;
        }

        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || uri.HostNameType == UriHostNameType.Unknown ||
                uri.Host.StartsWith('-') || uri.UserInfo.StartsWith('-'))
            {
                error = L.T("Geçersiz URL.", "Invalid URL.");
                return false;
            }

            if (uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length > 0)
            {
                error = L.T("URL'de kullanıcı adı/parola olmamalı; git kimlik yöneticisini kullanın.", "Do not put credentials in the URL; use the git credential manager.");
                return false;
            }

            if (uri.AbsolutePath.Trim('/').Length == 0)
            {
                error = L.T("URL bir depo yolu içermeli.", "URL must contain a repository path.");
                return false;
            }

            return true;
        }

        if (ScpLike().IsMatch(u))
        {
            return true;
        }

        error = L.T("Yalnızca https:// veya ssh (git@host:kullanıcı/depo) adresleri desteklenir.", "Only https:// or ssh (git@host:user/repo) URLs are supported.");
        return false;
    }

    /// <summary>Folder name for a clone: last path segment without ".git", reduced to safe characters.</summary>
    public static string FolderNameFor(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var u = url.Trim().TrimEnd('/');
        var last = u[(Math.Max(u.LastIndexOf('/'), u.LastIndexOf(':')) + 1)..];
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            last = last[..^4];
        }

        var safe = new string(last.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('.', '-', ' ');
        return safe.Length == 0 ? "repo" : safe;
    }

    /// <summary><c>user@host:owner/repo(.git)</c> – user and host restricted to safe characters, path must not start with '-' or '/'.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._\-]+@[A-Za-z0-9.\-]+:(?![-/])[A-Za-z0-9._~\-/]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpLike();
}
