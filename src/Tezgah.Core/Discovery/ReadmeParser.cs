using System.Text;
using System.Text.RegularExpressions;
using Tezgah.Core.Model;

namespace Tezgah.Core.Discovery;

/// <summary>A command suggested by a README code block, already tokenised.</summary>
internal sealed record ReadmeCommand(string Line, IReadOnlyList<string> Tokens, RunProfileKind Kind);

/// <summary>
/// Extracts a description and runnable single-line commands from README files. Pure text processing, nothing is executed.
/// </summary>
internal static partial class ReadmeParser
{
    public const int MaxCommands = 4;

    private static readonly string[] ReadmeNames = ["README.md", "readme.md", "README.markdown", "README.rst", "README.txt", "README"];

    private static readonly string[] SectionKeywords =
    [
        "run", "running", "usage", "quick start", "quickstart", "getting started", "get started", "install", "installation",
        "setup", "set up", "start", "launch", "how to use", "build", "development", "local development",
        "kurulum", "çalıştırma", "çalıştır", "kullanım", "başlangıç", "hızlı başlangıç", "yükleme",
    ];

    private static readonly HashSet<string> ShellLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        string.Empty, "bash", "sh", "shell", "console", "zsh", "powershell", "pwsh", "ps", "ps1", "cmd", "bat", "batch", "terminal", "text", "shell-session", "sh-session",
    };

    /// <summary>Programs whose README lines are never offered (POSIX-only, privilege, downloads, destructive or navigation).</summary>
    private static readonly HashSet<string> RejectedPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo", "su", "doas", "curl", "wget", "iwr", "irm", "iex", "invoke-webrequest", "invoke-restmethod", "invoke-expression",
        "sh", "bash", "zsh", "source", ".", "export", "set", "setx", "cd", "chmod", "chown", "rm", "del", "rmdir", "rd", "mv", "cp",
        "apt", "apt-get", "yum", "dnf", "pacman", "brew", "port", "snap", "git", "mkdir", "echo", "cat", "ls", "dir", "touch",
        "eval", "exec", "nohup", "open", "xdg-open", "start", "reg", "powershell", "pwsh", "cmd", "conda", "winget", "choco",
    };

    public static string? FindReadme(string directory)
    {
        foreach (var name in ReadmeNames)
        {
            var p = Path.Combine(directory, name);
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>First prose paragraph with markdown stripped, at most <paramref name="maxLength"/> characters.</summary>
    public static string? ExtractDescription(string markdown, int maxLength = 200)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new StringBuilder();
        var inFence = false;
        var inHtmlComment = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                if (paragraph.Length > 0)
                {
                    break;
                }

                continue;
            }

            if (inFence)
            {
                continue;
            }

            if (inHtmlComment)
            {
                if (line.Contains("-->", StringComparison.Ordinal))
                {
                    inHtmlComment = false;
                }

                continue;
            }

            if (line.StartsWith("<!--", StringComparison.Ordinal))
            {
                inHtmlComment = !line.Contains("-->", StringComparison.Ordinal);
                continue;
            }

            var isNoise = line.Length == 0 || line.StartsWith('#') || line.StartsWith('<') || line.StartsWith('|') ||
                          line.StartsWith("![", StringComparison.Ordinal) || line.StartsWith("[![", StringComparison.Ordinal) ||
                          line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("===", StringComparison.Ordinal) ||
                          line.StartsWith('>') || line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) ||
                          IsLinkOnlyLine(line);
            if (isNoise)
            {
                if (paragraph.Length > 0)
                {
                    break;
                }

                continue;
            }

            if (paragraph.Length > 0)
            {
                paragraph.Append(' ');
            }

            paragraph.Append(line);
        }

        if (paragraph.Length == 0)
        {
            return null;
        }

        var text = StripMarkdown(paragraph.ToString());
        if (text.Length == 0)
        {
            return null;
        }

        return text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";
    }

    /// <summary>Longest README line examined; longer lines are treated as noise (bounds regex work per line).</summary>
    internal const int MaxLineLength = 2000;

    /// <summary>
    /// Badge rows and language switchers ("[English](a) | [中文](b)"): nothing but links, images and separators.
    /// Linear: only negated-class regexes (Image, Link, HtmlTag) are used — the former nested lazy "link-only" pattern
    /// backtracked exponentially on a row of links followed by text.
    /// </summary>
    internal static bool IsLinkOnlyLine(string line)
    {
        if (line.Length > MaxLineLength)
        {
            return true;
        }

        try
        {
            var noTags = HtmlTag().Replace(line, string.Empty);
            var images = Image().Count(noTags);
            var noImages = Image().Replace(noTags, string.Empty); // "[![alt](img)](url)" collapses to "[](url)"
            var links = Link().Count(noImages);
            var rest = Link().Replace(noImages, string.Empty);
            if (links + images >= 1 && rest.All(c => char.IsWhiteSpace(c) || c is '|' or '·' or '•' or '-' or '/' or ',' or '–' or '—'))
            {
                return true;
            }

            var letters = rest.Count(char.IsLetterOrDigit);
            return (links >= 1 && letters == 0) || (links >= 2 && letters < 20);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    public static string StripMarkdown(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return StripMarkdownCore(text.Length > 8 * MaxLineLength ? text[..(8 * MaxLineLength)] : text);
        }
        catch (RegexMatchTimeoutException)
        {
            return string.Empty;
        }
    }

    private static string StripMarkdownCore(string text)
    {
        var t = Image().Replace(text, string.Empty);
        t = Link().Replace(t, "$1");
        t = HtmlTag().Replace(t, string.Empty);
        t = t.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);
        t = Emphasis().Replace(t, "$1");
        t = MultiSpace().Replace(t, " ");
        return t.Trim();
    }

    /// <summary>Single-line commands from fenced code blocks under run/usage/install headings (first <see cref="MaxCommands"/>).</summary>
    public static IReadOnlyList<ReadmeCommand> ExtractCommands(string markdown, int max = MaxCommands)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var result = new List<ReadmeCommand>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inRelevantSection = false;
        var inFence = false;
        var fenceRelevant = false;
        string fenceMarker = "```";
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (!inFence)
            {
                if (trimmed.StartsWith('#'))
                {
                    var heading = trimmed.TrimStart('#').Trim().TrimEnd('#').Trim();
                    inRelevantSection = IsRelevantHeading(heading);
                    continue;
                }

                if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                {
                    fenceMarker = trimmed[..3];
                    var lang = trimmed[3..].Trim().Split(' ', '{')[0];
                    inFence = true;
                    fenceRelevant = inRelevantSection && ShellLanguages.Contains(lang);
                }

                continue;
            }

            if (trimmed.StartsWith(fenceMarker, StringComparison.Ordinal))
            {
                inFence = false;
                continue;
            }

            if (!fenceRelevant || result.Count >= max)
            {
                continue;
            }

            var cmd = TryParseCommandLine(trimmed);
            if (cmd is not null && seen.Add(cmd.Line))
            {
                result.Add(cmd);
            }
        }

        return result;
    }

    private static bool IsRelevantHeading(string heading)
    {
        var h = StripMarkdown(heading).ToLowerInvariant();
        h = LeadingNumbering().Replace(h, string.Empty).Trim();
        foreach (var k in SectionKeywords)
        {
            if (h == k || h.StartsWith(k + " ", StringComparison.Ordinal) || h.EndsWith(" " + k, StringComparison.Ordinal) ||
                h.Contains(" " + k + " ", StringComparison.Ordinal) || h.StartsWith(k + ":", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Validates and tokenises one README line. Returns null for anything that is not a plain single command.</summary>
    public static ReadmeCommand? TryParseCommandLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var l = line.Trim();
        if (l.Length == 0 || l.StartsWith('#') || l.StartsWith("//", StringComparison.Ordinal) || l.StartsWith("REM ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // strip prompts: "$ npm start", "> npm start", "PS> npm start", "PS C:\x> npm start"
        var prompt = Prompt().Match(l);
        if (prompt.Success)
        {
            l = l[prompt.Length..].Trim();
        }

        if (l.Length == 0 || l.Length > 300)
        {
            return null;
        }

        // Shell operators, substitutions and line continuations are never accepted.
        if (l.Contains("&&", StringComparison.Ordinal) || l.Contains("||", StringComparison.Ordinal) ||
            l.IndexOfAny(['|', '>', '<', ';', '`', '&']) >= 0 || l.Contains("$(", StringComparison.Ordinal) ||
            l.EndsWith('\\') || l.EndsWith('^') || l.Contains('\t', StringComparison.Ordinal) ||
            l.Contains("${", StringComparison.Ordinal) || l.Contains('%', StringComparison.Ordinal))
        {
            return null;
        }

        var tokens = Tokenize(l);
        if (tokens is null || tokens.Count == 0)
        {
            return null;
        }

        var program = tokens[0];
        if (program.StartsWith('-') || program.StartsWith("./", StringComparison.Ordinal) || program.StartsWith('$') ||
            program.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) || program.Contains('=', StringComparison.Ordinal) ||
            RejectedPrograms.Contains(program) || program.Contains('/', StringComparison.Ordinal) ||
            !ProgramName().IsMatch(program))
        {
            return null;
        }

        // "<placeholder>" style arguments mean the line needs editing first.
        if (tokens.Any(t => t.Contains("your_", StringComparison.OrdinalIgnoreCase) || t.Contains("YOUR-", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return new ReadmeCommand(l, tokens, Classify(tokens));
    }

    private static RunProfileKind Classify(List<string> tokens)
    {
        var words = tokens.Select(t => t.ToLowerInvariant()).ToList();
        if (words.Contains("install") || words.Contains("sync") || words.Contains("restore") || words.Contains("venv") ||
            (words.Contains("ci") && words[0] is "npm"))
        {
            return RunProfileKind.Setup;
        }

        if (words.Contains("test") || words.Contains("pytest") || words[0] == "pytest")
        {
            return RunProfileKind.Test;
        }

        if (words.Contains("build") || words.Contains("compile") || words.Contains("publish"))
        {
            return RunProfileKind.Build;
        }

        return RunProfileKind.Run;
    }

    /// <summary>Windows-style tokenizer that respects double quotes (CommandLineToArgvW rules) and, for README convenience, single quotes.</summary>
    public static List<string>? Tokenize(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!line.Contains('\'', StringComparison.Ordinal))
        {
            return WindowsCommandLine.SplitArguments(line);
        }

        var result = new List<string>();
        var sb = new StringBuilder();
        var inToken = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c is '\'' or '"')
            {
                var end = line.IndexOf(c, i + 1);
                if (end < 0)
                {
                    return null; // unbalanced quotes
                }

                sb.Append(line, i + 1, end - i - 1);
                i = end;
                inToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                    inToken = false;
                }

                continue;
            }

            sb.Append(c);
            inToken = true;
        }

        if (inToken)
        {
            result.Add(sb.ToString());
        }

        return result;
    }

    [GeneratedRegex(@"^(?:\$|>|PS>|PS [A-Za-z]:[^>]*>|C:\\[^>]*>)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex Prompt();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProgramName();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.CultureInvariant, 250)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant, 250)]
    private static partial Regex Link();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant, 250)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"(?<![\w*])[*_]([^*_]+)[*_](?![\w*])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant, 250)]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"^[\d.\s)]+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumbering();
}
