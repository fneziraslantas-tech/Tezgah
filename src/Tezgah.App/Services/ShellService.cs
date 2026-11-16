using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Tezgah.App.Services;

/// <summary>
/// Opens URLs, folders, terminals and editors. Kept in one file so it can be swapped for
/// <c>Tezgah.Core.Execution.ShellOpener</c> once the core provides it.
/// Rules: only absolute http/https URLs are shell-executed; folders must exist; external programs are
/// started with <see cref="ProcessStartInfo.ArgumentList"/> (never a command string, never cmd.exe).
/// </summary>
public interface IShellService
{
    bool OpenUrl(Uri url);

    bool OpenFolder(string path);

    /// <summary>Windows Terminal (<c>wt.exe -d path</c>) when available, else pwsh / Windows PowerShell.</summary>
    bool OpenTerminal(string path);

    bool IsVsCodeAvailable { get; }

    bool OpenInVsCode(string path);
}

public sealed class ShellService(ILogger<ShellService> logger) : IShellService
{
    private readonly Lazy<string?> _vsCode = new(FindVsCode);

    public bool IsVsCodeAvailable => _vsCode.Value is not null;

    public bool OpenUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            logger.LogWarning("Refused to open non-http URL {Url}", url);
            return false;
        }

        return TryStart(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }

    public bool OpenFolder(string path)
    {
        var full = Normalize(path);
        if (full is null)
        {
            return false;
        }

        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
        psi.ArgumentList.Add(full);
        return TryStart(psi);
    }

    public bool OpenTerminal(string path)
    {
        var full = Normalize(path);
        if (full is null)
        {
            return false;
        }

        var wt = ResolveExecutable("wt.exe");
        if (wt is not null)
        {
            var psi = new ProcessStartInfo(wt) { UseShellExecute = false };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(full);
            return TryStart(psi);
        }

        var shell = ResolveExecutable("pwsh.exe") ?? ResolveExecutable("powershell.exe");
        if (shell is null)
        {
            return false;
        }

        var ps = new ProcessStartInfo(shell) { UseShellExecute = true, WorkingDirectory = full };
        ps.ArgumentList.Add("-NoLogo");
        return TryStart(ps);
    }

    public bool OpenInVsCode(string path)
    {
        var full = Normalize(path);
        var code = _vsCode.Value;
        if (full is null || code is null)
        {
            return false;
        }

        var psi = new ProcessStartInfo(code) { UseShellExecute = false };
        psi.ArgumentList.Add(full);
        return TryStart(psi);
    }

    private string? Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full))
            {
                return full;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            logger.LogWarning(ex, "Invalid path {Path}", path);
        }

        return null;
    }

    private bool TryStart(ProcessStartInfo psi)
    {
        try
        {
            using var p = Process.Start(psi);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogWarning(ex, "Could not start {File}", psi.FileName);
            return false;
        }
    }

    /// <summary>Resolves Code.exe directly (never code.cmd, which would need cmd.exe).</summary>
    private static string? FindVsCode()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(pf, "Microsoft VS Code", "Code.exe"),
        ];
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null)
        {
            return found;
        }

        // code.cmd lives in "<install>\bin"; the real executable is one level up.
        var cmd = ResolveExecutable("code.cmd");
        if (cmd is not null)
        {
            var exe = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(cmd)!)!, "Code.exe");
            if (File.Exists(exe))
            {
                return exe;
            }
        }

        return null;
    }

    internal static string? ResolveExecutable(string fileName)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"));
        foreach (var dir in dirs)
        {
            try
            {
                if (!Path.IsPathFullyQualified(dir))
                {
                    continue;
                }

                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // malformed PATH entry
            }
        }

        return null;
    }
}
