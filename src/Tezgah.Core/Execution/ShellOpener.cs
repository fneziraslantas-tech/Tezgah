using System.ComponentModel;
using System.Diagnostics;
using Tezgah.Core.Model;
using Tezgah.Core.Safety;

namespace Tezgah.Core.Execution;

/// <summary>
/// The ONLY place in Tezgah that uses <c>UseShellExecute = true</c>. Opens web links in the default browser and folders in
/// Explorer after validating them, so a URL or path coming from a scanned repository (package.json "homepage", README...)
/// can never launch a program, a <c>file:</c>/<c>ms-*:</c>/<c>search-ms:</c> protocol handler or a network share.
/// </summary>
public static class ShellOpener
{
    /// <summary>True when <paramref name="url"/> is an absolute http(s) URL without embedded credentials.</summary>
    public static bool IsSafeUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsControl) || url.Length > 4096)
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if ((parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) || parsed.UserInfo.Length > 0 || parsed.IsUnc || parsed.IsFile)
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>Opens an http/https URL in the default browser. Returns false with a reason when refused or failed.</summary>
    public static bool OpenUrl(string? url, out string? error)
    {
        if (!IsSafeUrl(url, out var uri))
        {
            error = L.T("Yalnızca http/https bağlantıları açılabilir.", "Only http/https links can be opened.");
            return false;
        }

        return ShellExecute(uri!.AbsoluteUri, out error);
    }

    /// <summary>Opens an existing local folder in Explorer. The folder must pass the path guard's Read check.</summary>
    public static bool OpenFolder(string? path, out string? error)
    {
        var check = PathGuard.Default.Check(path ?? string.Empty, PathOperation.Read);
        if (!check.Allowed)
        {
            error = check.Reason;
            return false;
        }

        var full = Path.GetFullPath(path!);
        if (!Directory.Exists(full))
        {
            error = L.T("Klasör bulunamadı.", "Folder not found.");
            return false;
        }

        // Trailing separator makes the shell treat it as a folder even if the name looks like "tool.exe".
        return ShellExecute(Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar, out error);
    }

    private static bool ShellExecute(string target, out string? error)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Verb = "open" });
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            error = ex.Message;
            return false;
        }
    }
}
