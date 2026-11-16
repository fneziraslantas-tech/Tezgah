using System.Text;

namespace Tezgah.Core.Discovery;

/// <summary>
/// Splits command lines the same way <c>CommandLineToArgvW</c> / the MSVC runtime does.
/// </summary>
internal static class WindowsCommandLine
{
    /// <summary>
    /// Splits a full command line. The first token (program name) follows the special CommandLineToArgvW rule:
    /// a quoted name runs to the next quote, an unquoted one to the next whitespace, and backslashes are literal.
    /// </summary>
    public static List<string> Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var result = new List<string>();
        var i = 0;
        SkipWhitespace(commandLine, ref i);
        if (i >= commandLine.Length)
        {
            return result;
        }

        result.Add(ReadProgramName(commandLine, ref i));
        result.AddRange(SplitArguments(commandLine[i..]));
        return result;
    }

    /// <summary>Reads the program-name token starting at <paramref name="i"/>.</summary>
    public static string ReadProgramName(string s, ref int i)
    {
        var sb = new StringBuilder();
        if (i < s.Length && s[i] == '"')
        {
            i++;
            while (i < s.Length && s[i] != '"')
            {
                sb.Append(s[i++]);
            }

            if (i < s.Length)
            {
                i++; // closing quote
            }

            // Anything glued to the closing quote belongs to the same token.
            while (i < s.Length && !char.IsWhiteSpace(s[i]))
            {
                sb.Append(s[i++]);
            }
        }
        else
        {
            while (i < s.Length && !char.IsWhiteSpace(s[i]))
            {
                sb.Append(s[i++]);
            }
        }

        return sb.ToString();
    }

    /// <summary>Splits an argument string (everything after the program name) using the MSVC rules.</summary>
    public static List<string> SplitArguments(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var result = new List<string>();
        var i = 0;
        while (true)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length)
            {
                return result;
            }

            var sb = new StringBuilder();
            var inQuotes = false;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '\\')
                {
                    var start = i;
                    while (i < s.Length && s[i] == '\\')
                    {
                        i++;
                    }

                    var count = i - start;
                    if (i < s.Length && s[i] == '"')
                    {
                        sb.Append('\\', count / 2);
                        if (count % 2 == 1)
                        {
                            sb.Append('"');
                            i++;
                        }
                    }
                    else
                    {
                        sb.Append('\\', count);
                    }

                    continue;
                }

                if (c == '"')
                {
                    if (inQuotes && i + 1 < s.Length && s[i + 1] == '"')
                    {
                        sb.Append('"'); // "" inside quotes is a literal quote
                        i += 2;
                        continue;
                    }

                    inQuotes = !inQuotes;
                    i++;
                    continue;
                }

                if (!inQuotes && char.IsWhiteSpace(c))
                {
                    break;
                }

                sb.Append(c);
                i++;
            }

            result.Add(sb.ToString());
        }
    }

    private static void SkipWhitespace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }
    }
}
