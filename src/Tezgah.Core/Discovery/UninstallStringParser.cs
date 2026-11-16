using System.Text.RegularExpressions;

namespace Tezgah.Core.Discovery;

/// <summary>An executable plus its separate arguments.</summary>
internal sealed record ParsedCommandLine(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// Parses registry <c>UninstallString</c> / <c>QuietUninstallString</c> values into an executable and argument list.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Environment variables (<c>%ProgramFiles%</c>...) are expanded.</item>
/// <item>Quoted program paths and arguments follow the <c>CommandLineToArgvW</c> rules.</item>
/// <item>Unquoted program paths containing spaces (<c>C:\Program Files\X\unins000.exe /SILENT</c>) are resolved by
/// probing <c>File.Exists</c> on growing prefixes, exactly like <c>CreateProcess</c> does.</item>
/// <item><c>MsiExec.exe /I{GUID}</c> (repair/modify) is turned into <c>/X{GUID}</c> (remove).</item>
/// </list>
/// </remarks>
internal static partial class UninstallStringParser
{
    private static readonly string[] ExecutableExtensions = [".exe", ".com", ".bat", ".cmd"];

    public static ParsedCommandLine? Parse(string? uninstallString) => Parse(uninstallString, File.Exists);

    public static ParsedCommandLine? Parse(string? uninstallString, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (string.IsNullOrWhiteSpace(uninstallString))
        {
            return null;
        }

        var s = Environment.ExpandEnvironmentVariables(uninstallString.Trim());
        string fileName;
        string rest;
        if (s[0] == '"')
        {
            var i = 0;
            fileName = WindowsCommandLine.ReadProgramName(s, ref i);
            rest = s[i..];
        }
        else
        {
            (fileName, rest) = SplitUnquoted(s, fileExists);
        }

        fileName = fileName.Trim();
        if (fileName.Length == 0)
        {
            return null;
        }

        var args = WindowsCommandLine.SplitArguments(rest);
        if (IsMsiExec(fileName))
        {
            args = FixMsiArguments(args);
        }

        return new ParsedCommandLine(fileName, args);
    }

    /// <summary>For unquoted strings, finds the longest whitespace-delimited prefix that is an existing file.</summary>
    private static (string FileName, string Remainder) SplitUnquoted(string s, Func<string, bool> fileExists)
    {
        var boundaries = new List<int>();
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i]) && i > 0 && !char.IsWhiteSpace(s[i - 1]))
            {
                boundaries.Add(i);
            }
        }

        boundaries.Add(s.Length);

        // CreateProcess semantics: try the shortest prefix first ("C:\Program" then "C:\Program Files\..."),
        // but only accept prefixes that are real files (optionally with an implied .exe).
        foreach (var end in boundaries)
        {
            var candidate = s[..end];
            if (ExistsAsExecutable(candidate, fileExists, out var resolved))
            {
                return (resolved, s[end..]);
            }
        }

        // Fallback: prefix ending in a known executable extension (file may be on another machine's layout or missing).
        foreach (var end in boundaries)
        {
            var candidate = s[..end];
            if (ExecutableExtensions.Any(e => candidate.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                return (candidate, s[end..]);
            }
        }

        var first = boundaries[0];
        return (s[..first], s[first..]);
    }

    private static bool ExistsAsExecutable(string candidate, Func<string, bool> fileExists, out string resolved)
    {
        resolved = candidate;
        if (!Path.IsPathRooted(candidate))
        {
            // bare names like "MsiExec.exe" or "rundll32.exe": accept when they look like an executable
            if (candidate.IndexOfAny(['\\', '/']) < 0 &&
                ExecutableExtensions.Any(e => candidate.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }

        if (fileExists(candidate))
        {
            return true;
        }

        if (!Path.HasExtension(candidate) && fileExists(candidate + ".exe"))
        {
            resolved = candidate + ".exe";
            return true;
        }

        return false;
    }

    public static bool IsMsiExec(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("msiexec", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> FixMsiArguments(List<string> args)
    {
        var result = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            var m = MsiInstallSwitch().Match(a);
            if (m.Success)
            {
                // "/I{GUID}" -> "/X{GUID}", "/I" "{GUID}" -> "/X" "{GUID}"
                result.Add(a[0] + "X" + m.Groups["rest"].Value);
                continue;
            }

            result.Add(a);
        }

        return result;
    }

    [GeneratedRegex(@"^[/-][IiFf](?<rest>\{[0-9A-Fa-f\-]+\}|)$", RegexOptions.CultureInvariant)]
    private static partial Regex MsiInstallSwitch();
}
