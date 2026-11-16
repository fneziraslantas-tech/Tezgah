namespace Tezgah.Core.Model;

public enum UninstallStepKind
{
    /// <summary>Stop processes (tracked by Tezgah or whose executable lives under the item path).</summary>
    StopProcesses,

    /// <summary>Run the item's own uninstaller / package-manager removal command.</summary>
    RunCommand,

    /// <summary>Move a directory to the Recycle Bin (or delete permanently when settings say so).</summary>
    RemoveDirectory,

    /// <summary>Remove a Start-menu / desktop shortcut pointing into the item.</summary>
    RemoveShortcut,

    /// <summary>Forget the item in the catalog.</summary>
    RemoveCatalogEntry,
}

public sealed record UninstallStep
{
    public required UninstallStepKind Kind { get; init; }
    public required string Description { get; init; }

    /// <summary>Target path for RemoveDirectory / RemoveShortcut / StopProcesses.</summary>
    public string? Path { get; init; }

    public CommandSpec? Command { get; init; }
    public long? SizeBytes { get; init; }

    /// <summary>Optional steps can be unticked by the user (e.g. related venv elsewhere).</summary>
    public bool Optional { get; init; }

    /// <summary>
    /// Initial tick state of an optional step. False for steps whose ownership or contents are uncertain (installed-app
    /// leftovers, name-matched virtualenvs): the user has to opt in.
    /// </summary>
    public bool DefaultSelected { get; init; } = true;
}

public enum WarningLevel
{
    Info,
    Warning,

    /// <summary>Execution refused unless the user types the item name to confirm.</summary>
    Danger,

    /// <summary>Execution is impossible (e.g. path guard refused the target).</summary>
    Blocker,
}

public sealed record PlanWarning(WarningLevel Level, string Message);

public sealed record UninstallPlan
{
    public required string ItemId { get; init; }
    public required string ItemName { get; init; }
    public IReadOnlyList<UninstallStep> Steps { get; init; } = [];
    public IReadOnlyList<PlanWarning> Warnings { get; init; } = [];
    public bool IsBlocked => Warnings.Any(w => w.Level == WarningLevel.Blocker);
    public bool RequiresTypedConfirmation => Warnings.Any(w => w.Level == WarningLevel.Danger);
    public long TotalBytes => Steps.Sum(s => s.SizeBytes ?? 0);
}

public sealed record StepOutcome(UninstallStep Step, bool Success, string? Error);

public sealed record UninstallResult(IReadOnlyList<StepOutcome> Outcomes)
{
    public bool Success => Outcomes.All(o => o.Success);

    /// <summary>Set when the work was done but its audit record could not be written.</summary>
    public string? AuditWarning { get; init; }
}

public enum PathOperation
{
    Read,
    Execute,
    Delete,
}

public sealed record PathCheck(bool Allowed, string? Reason)
{
    public static PathCheck Ok { get; } = new(true, null);
    public static PathCheck Deny(string reason) => new(false, reason);
}
