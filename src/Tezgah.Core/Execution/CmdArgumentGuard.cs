using System.Text;
using System.Text.RegularExpressions;

namespace Tezgah.Core.Execution;

/// <summary>Thrown when arguments for a .cmd/.bat target cannot be passed to cmd.exe safely.</summary>
public sealed class UnsafeCommandArgumentException : ArgumentException
{
    public UnsafeCommandArgumentException()
    {
    }

    public UnsafeCommandArgumentException(string message)
        : base(message)
    {
    }

    public UnsafeCommandArgumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Builds a safe cmd.exe command line for batch-file targets (npm.cmd, yarn.cmd, gradlew.bat, mvnw.cmd...).
/// <para>
/// Why this exists: CreateProcess cannot run a .cmd/.bat directly; Windows (and .NET's Process when given one) silently
/// runs it through <c>cmd.exe /c</c>, which re-parses the whole command line with its own rules. The MSVCRT-style quoting
/// that <c>ArgumentList</c> applies means nothing to cmd: <c>&amp; | &lt; &gt; ( ) ^</c> still split or redirect the command,
/// <c>%VAR%</c> and (with delayed expansion) <c>!VAR!</c> are expanded even inside quotes, and a <c>"</c> inside an argument
/// toggles cmd's quote state so the rest of the line becomes live syntax. This is the "BatBadBut" class of bugs
/// (CVE-2024-24576 in Rust, and the same issue in Node, Python, Go, Java...). There is no escaping scheme that is correct
/// for every batch file, because the batch file itself re-expands %1/%* again.
/// </para>
/// <para>
/// So instead of escaping we use a strict allow-list: we only build the command line when every argument consists of
/// characters that have no meaning to cmd. Arguments with spaces (paths) are allowed and wrapped in quotes, provided
/// they contain none of <c>" % ! ^ &amp; | &lt; &gt; ( )</c>. Anything else throws <see cref="UnsafeCommandArgumentException"/> —
/// the user can still run the command from their own terminal, but Tezgah will not be the one to mis-quote it.
/// </para>
/// <para>
/// The resulting command line is <c>cmd.exe /d /v:off /s /c ""C:\full\path\npm.cmd" arg1 "arg 2""</c>:
/// /d skips AutoRun registry commands, /v:off disables delayed (!VAR!) expansion, /s makes cmd strip exactly the outer
/// pair of quotes so the inner quoting survives. cmd.exe is always taken from the System32 directory, never from PATH or
/// the working directory.
/// </para>
/// </summary>
internal static partial class CmdArgumentGuard
{
    /// <summary>Path to the system cmd.exe (never resolved through PATH or the current directory).</summary>
    public static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    public static bool IsBatchFile(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <see cref="BuildCmdArguments"/> accepts <paramref name="path"/> as the batch file to run.</summary>
    public static bool IsSafeScriptPath(string path) =>
        !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) && IsBatchFile(path) && ScriptPath().IsMatch(path);

    /// <summary>True when the argument can be passed through cmd.exe without being re-interpreted.</summary>
    public static bool IsSafeArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (PlainArgument().IsMatch(argument))
        {
            return true;
        }

        // Quoted form: printable text with spaces but none of cmd's metacharacters or a quote.
        return argument.Length > 0 && argument.Trim().Length > 0 && QuotableArgument().IsMatch(argument);
    }

    /// <summary>
    /// Returns the raw argument string for <c>cmd.exe</c> (to be used as <c>ProcessStartInfo.Arguments</c>, not ArgumentList,
    /// because cmd does not follow MSVCRT quoting rules). Throws when the script path or any argument is unsafe.
    /// </summary>
    public static string BuildCmdArguments(string batchFullPath, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(batchFullPath);
        ArgumentNullException.ThrowIfNull(arguments);

        if (!Path.IsPathFullyQualified(batchFullPath) || !IsBatchFile(batchFullPath))
        {
            throw new UnsafeCommandArgumentException(L.T("Toplu iş dosyası tam yolla verilmeli.", "The batch file must be given as a full path."));
        }

        // Inside quotes cmd still expands % and ! and a quote would end the quoted section; parentheses and & are inert
        // inside quotes but we keep the rule simple: the script path may contain spaces and parentheses, nothing else special.
        if (!ScriptPath().IsMatch(batchFullPath))
        {
            throw new UnsafeCommandArgumentException(L.T(
                $"Toplu iş dosyasının yolu güvenli olmayan karakterler içeriyor: {batchFullPath}",
                $"The batch file path contains unsafe characters: {batchFullPath}"));
        }

        var builder = new StringBuilder("/d /v:off /s /c \"\"");
        builder.Append(batchFullPath).Append('"');
        foreach (var argument in arguments)
        {
            if (!IsSafeArgument(argument))
            {
                throw new UnsafeCommandArgumentException(L.T(
                    $"Bu argüman cmd.exe üzerinden güvenle iletilemez: {Describe(argument)}. Komutu kendi terminalinizden çalıştırın.",
                    $"This argument cannot be passed through cmd.exe safely: {Describe(argument)}. Run the command from your own terminal."));
            }

            builder.Append(' ');

            // ',' ';' '=' and spaces are argument delimiters for the batch file's %1..%9; quote so they stay one argument.
            if (argument.AsSpan().IndexOfAny(" ,;=\t") >= 0)
            {
                builder.Append('"').Append(argument).Append('"');
            }
            else
            {
                builder.Append(argument);
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string Describe(string argument)
    {
        var printable = new string(argument.Select(c => char.IsControl(c) ? '·' : c).ToArray());
        return printable.Length > 80 ? printable[..80] + "…" : printable;
    }

    // Word characters (Unicode letters/digits/underscore) and - . : / \ = @ + , — none of which cmd interprets.
    [GeneratedRegex(@"^[\w\-.:/\\=@+,]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainArgument();

    // Same set plus spaces/semicolons/tilde/apostrophe/#/$/[]{} for quoted paths. Excludes " % ! ^ & | < > ( ) and control characters.
    [GeneratedRegex(@"^[\w\-.:/\\=@+, ;~'#$\[\]{}]+$", RegexOptions.CultureInvariant)]
    private static partial Regex QuotableArgument();

    [GeneratedRegex(@"^[A-Za-z]:\\[\w\-.\\ ()+,;=@~'#$\[\]{}]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptPath();
}
