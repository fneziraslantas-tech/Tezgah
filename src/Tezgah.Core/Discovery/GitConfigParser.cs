using System.Text;

namespace Tezgah.Core.Discovery;

/// <summary>One <c>section[.subsection].name = value</c> entry of a git config file. Section and name are lower-case.</summary>
internal sealed record GitConfigEntry(string Section, string? Subsection, string Name, string? Value)
{
    /// <summary>Canonical key: <c>section.name</c> or <c>section.subsection.name</c> (subsection keeps its case).</summary>
    public string Key => Subsection is null ? $"{Section}.{Name}" : $"{Section}.{Subsection}.{Name}";
}

/// <summary>
/// Reader for git's config file syntax (also used for <c>.gitmodules</c>). Never resolves includes.
/// <list type="bullet">
/// <item><b>Strict</b> (<see cref="GitRepoGuard"/>): the whole file must be understood. Value line continuations
/// (<c>\</c> at the end of a line), unknown escapes, unterminated quotes or headers and anything else unexpected make it
/// fail — a guard that cannot see what git will see must refuse.</item>
/// <item><b>Tolerant</b> (<see cref="GitMetadataReader"/>): skips lines it does not understand. Safe there because the
/// result is only displayed or compared; nothing is executed or opened from it.</item>
/// </list>
/// </summary>
internal static class GitConfigParser
{
    public static bool TryParse(string? text, bool strict, out List<GitConfigEntry> entries, out string? error)
    {
        entries = [];
        error = null;
        if (text is null)
        {
            return true;
        }

        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            error = "NUL byte in config";
            if (strict)
            {
                return false;
            }
        }

        var lines = text.Split('\n');
        string? section = null;
        string? subsection = null;
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n].TrimEnd('\r');
            var i = SkipSpace(line, 0);
            if (i >= line.Length || line[i] is '#' or ';')
            {
                continue;
            }

            if (line[i] == '[')
            {
                if (!TryParseHeader(line, ref i, out var s, out var sub, out var why))
                {
                    if (strict)
                    {
                        error = $"line {n + 1}: {why}";
                        return false;
                    }

                    section = null; // entries under a header we could not read are dropped
                    continue;
                }

                section = s;
                subsection = sub;
                i = SkipSpace(line, i);
                if (i >= line.Length || line[i] is '#' or ';')
                {
                    continue;
                }
            }

            if (section is null)
            {
                if (strict)
                {
                    error = $"line {n + 1}: entry outside a section";
                    return false;
                }

                continue;
            }

            var keyStart = i;
            if (!char.IsAsciiLetter(line[i]))
            {
                if (strict)
                {
                    error = $"line {n + 1}: invalid key";
                    return false;
                }

                continue;
            }

            while (i < line.Length && (char.IsAsciiLetterOrDigit(line[i]) || line[i] == '-'))
            {
                i++;
            }

            var name = line[keyStart..i].ToLowerInvariant();
            i = SkipSpace(line, i);
            if (i >= line.Length || line[i] is '#' or ';')
            {
                entries.Add(new GitConfigEntry(section, subsection, name, null)); // bare key = true
                continue;
            }

            if (line[i] != '=')
            {
                if (strict)
                {
                    error = $"line {n + 1}: expected '='";
                    return false;
                }

                continue;
            }

            var (value, ok, consumedLines, why2) = ParseValue(lines, n, i + 1, strict);
            if (!ok)
            {
                if (strict)
                {
                    error = $"line {n + 1}: {why2}";
                    return false;
                }

                n += consumedLines;
                continue;
            }

            n += consumedLines;
            entries.Add(new GitConfigEntry(section, subsection, name, value));
        }

        return true;
    }

    private static int SkipSpace(string line, int i)
    {
        while (i < line.Length && line[i] is ' ' or '\t')
        {
            i++;
        }

        return i;
    }

    private static bool TryParseHeader(string line, ref int i, out string section, out string? subsection, out string? why)
    {
        section = string.Empty;
        subsection = null;
        why = null;
        i++; // '['
        var start = i;
        while (i < line.Length && (char.IsAsciiLetterOrDigit(line[i]) || line[i] is '-' or '.'))
        {
            i++;
        }

        if (i == start || i >= line.Length)
        {
            why = "invalid section header";
            return false;
        }

        var name = line[start..i];
        if (line[i] == ']')
        {
            i++;
            var dot = name.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                section = name.ToLowerInvariant();
            }
            else
            {
                // deprecated [section.subsection] form: subsection is lower-cased by git
                section = name[..dot].ToLowerInvariant();
                subsection = name[(dot + 1)..].ToLowerInvariant();
                if (section.Length == 0 || subsection.Length == 0)
                {
                    why = "invalid section header";
                    return false;
                }
            }

            return true;
        }

        if (line[i] is not (' ' or '\t') || name.Contains('.', StringComparison.Ordinal))
        {
            why = "invalid section header";
            return false;
        }

        i = SkipSpace(line, i);
        if (i >= line.Length || line[i] != '"')
        {
            why = "invalid subsection";
            return false;
        }

        i++;
        var sub = new StringBuilder();
        while (true)
        {
            if (i >= line.Length)
            {
                why = "unterminated subsection";
                return false;
            }

            var c = line[i++];
            if (c == '"')
            {
                break;
            }

            if (c == '\\')
            {
                if (i >= line.Length || line[i] is not ('"' or '\\'))
                {
                    why = "unsupported escape in subsection";
                    return false;
                }

                c = line[i++];
            }

            sub.Append(c);
        }

        if (i >= line.Length || line[i] != ']')
        {
            why = "invalid section header";
            return false;
        }

        i++;
        section = name.ToLowerInvariant();
        subsection = sub.ToString();
        return true;
    }

    /// <summary>Parses a value starting at <paramref name="start"/> of line <paramref name="n"/>. Returns extra lines consumed.</summary>
    private static (string Value, bool Ok, int ConsumedLines, string? Why) ParseValue(string[] lines, int n, int start, bool strict)
    {
        var sb = new StringBuilder();
        var committed = 0; // length without trailing unquoted whitespace
        var quoted = false;
        var consumed = 0;
        var line = lines[n].TrimEnd('\r');
        var i = SkipSpace(line, start);
        while (true)
        {
            if (i >= line.Length)
            {
                break;
            }

            var c = line[i++];
            if (c == '"')
            {
                quoted = !quoted;
                committed = sb.Length;
                continue;
            }

            if (!quoted && c is '#' or ';')
            {
                break;
            }

            if (c == '\\')
            {
                if (i >= line.Length)
                {
                    // Line continuation: git joins the next line into this value.
                    if (strict || n + consumed + 1 >= lines.Length)
                    {
                        return (string.Empty, false, consumed, "value continues on the next line");
                    }

                    consumed++;
                    line = lines[n + consumed].TrimEnd('\r');
                    i = 0;
                    continue;
                }

                var e = line[i++];
                char? mapped = e switch
                {
                    '\\' => '\\',
                    '"' => '"',
                    'n' => '\n',
                    't' => '\t',
                    'b' => '\b',
                    _ => null,
                };
                if (mapped is null)
                {
                    return (string.Empty, false, consumed, "invalid escape");
                }

                sb.Append(mapped.Value);
                committed = sb.Length;
                continue;
            }

            sb.Append(c);
            if (quoted || c is not (' ' or '\t'))
            {
                committed = sb.Length;
            }
        }

        if (quoted)
        {
            return (string.Empty, false, consumed, "unterminated quote");
        }

        return (sb.ToString(0, committed), true, consumed, null);
    }
}
