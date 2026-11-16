namespace Tezgah.Core.Model;

/// <summary>
/// A process invocation. Security rule: never built by string concatenation and never passed through a shell.
/// <see cref="FileName"/> is the executable, every argument is a separate element of <see cref="Arguments"/>
/// (mapped to <c>ProcessStartInfo.ArgumentList</c>).
/// </summary>
public sealed record CommandSpec
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public required string WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Short commands normally end when their first process exits (leftover children are killed). When true the command
    /// is only finished once every process it started has exited — needed for uninstallers that hand the real work to a
    /// relaunched copy of themselves (NSIS, Inno Setup) and exit at once.
    /// </summary>
    public bool WaitForProcessTree { get; init; }

    /// <summary>Human-readable preview shown to the user before execution (quoted like a command line).</summary>
    public string Display =>
        string.Join(' ', new[] { FileName }.Concat(Arguments).Select(a =>
            a.Length == 0 || a.Any(c => char.IsWhiteSpace(c) || c == '"') ? "\"" + a.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : a));

    /// <summary>
    /// True when both specs start exactly the same process: executable, every argument, working directory, environment
    /// and tree-wait flag compared ordinally (the record's own equality compares the collections by reference).
    /// </summary>
    public static bool IsSameInvocation(CommandSpec? a, CommandSpec? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (ReferenceEquals(a, b))
        {
            return true;
        }

        var envA = a.Environment ?? new Dictionary<string, string>();
        var envB = b.Environment ?? new Dictionary<string, string>();
        return string.Equals(a.FileName, b.FileName, StringComparison.Ordinal)
            && string.Equals(a.WorkingDirectory, b.WorkingDirectory, StringComparison.Ordinal)
            && a.WaitForProcessTree == b.WaitForProcessTree
            && (a.Arguments ?? []).SequenceEqual(b.Arguments ?? [], StringComparer.Ordinal)
            && envA.Count == envB.Count
            && envA.All(kv => envB.TryGetValue(kv.Key, out var v) && string.Equals(kv.Value, v, StringComparison.Ordinal));
    }
}

public enum RunProfileKind
{
    Run,
    Setup,
    Build,
    Test,
}

/// <summary>A named command a user can launch for an item ("npm run dev", "python main.py", "dotnet run"...).</summary>
public sealed record RunProfile
{
    /// <summary>Unique within the item, e.g. "npm:dev".</summary>
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required RunProfileKind Kind { get; init; }
    public required CommandSpec Command { get; init; }

    /// <summary>Which detector produced it ("node", "python", "user"...).</summary>
    public required string Origin { get; init; }

    /// <summary>True for the profile launched by the main "Run" button.</summary>
    public bool IsDefault { get; init; }
}

/// <summary>Result of a short, captured command.</summary>
public sealed record CommandResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

/// <summary>One line of output from a running process.</summary>
public sealed record OutputLine(DateTimeOffset At, string Text, bool IsError);
