using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Discovery;
using Tezgah.Core.Execution;
using Tezgah.Core.Interop;
using Tezgah.Core.Model;
using Tezgah.Core.Safety;

namespace Tezgah.Core.Uninstall;

/// <summary>Thrown when an uninstall plan requires the item name to be typed and the confirmation does not match.</summary>
public sealed class UninstallConfirmationException : InvalidOperationException
{
    public UninstallConfirmationException()
        : base(L.T("Onay için öğenin adını tam olarak yazın.", "Type the item name exactly to confirm."))
    {
    }

    public UninstallConfirmationException(string message)
        : base(message)
    {
    }

    public UninstallConfirmationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>True when the folder changed after planning (new unsaved work): the caller should re-plan.</summary>
    public bool PlanChanged { get; init; }
}

/// <summary>
/// Builds and executes uninstall plans. Safety properties:
/// <list type="bullet">
/// <item>Only plans produced by <see cref="PlanAsync"/> of this instance are executed. The plan's steps, warnings and
/// confirmation requirement are snapshotted at planning time and execution uses that snapshot only.</item>
/// <item>Blocked plans never run; Danger plans need the item name typed exactly. Folder plans re-read the git state right
/// before running: new unsaved work makes the execution refuse and ask for a new plan.</item>
/// <item>The path guard is re-run immediately before every deletion (inside <see cref="FileRemover"/> as well).</item>
/// <item>Installed-app leftovers are only offered for folders that verifiably belong to the app (opt-in), are re-checked
/// after the vendor uninstaller ran, and are never deleted while the uninstaller still appears unfinished.</item>
/// <item>Steps run sequentially and stop at the first failing mandatory step; the catalog entry is removed last.</item>
/// <item>An audit entry is written before anything is touched (no audit, no deletion) and another one at the end.</item>
/// </list>
/// </summary>
internal sealed partial class UninstallService(
    ICatalogService catalog,
    IGitClient git,
    PathGuard guard,
    FileRemover remover,
    ICommandExecutor executor,
    IRunService runService,
    IProcessFinder processFinder,
    UninstallOptions options) : IUninstallService
{
    /// <summary>Exit codes that mean "uninstalled" for MSI/vendor uninstallers (3010/1641 = reboot required/initiated).</summary>
    private static readonly int[] SuccessExitCodes = [0, 3010, 1641];

    /// <summary>Signs of user data that must never be swept away as "leftovers".</summary>
    private static readonly HashSet<string> DataMarkerFiles = new(StringComparer.OrdinalIgnoreCase) { "PG_VERSION", "postmaster.pid", "ibdata1", "my.ini", ".git" };
    private static readonly HashSet<string> DataMarkerDirectories = new(StringComparer.OrdinalIgnoreCase) { "data", "db", "database", "databases", "backup", "backups", ".git" };
    private static readonly string[] DataMarkerExtensions = [".mdf", ".ldf", ".db", ".sqlite", ".sqlite3", ".kdbx"];

    /// <summary>Regenerable folders never reported as "ignored local files".</summary>
    private static readonly HashSet<string> RegenerableDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".venv", "venv", "env", "__pycache__", "target", "dist", "build", "out", ".gradle", ".next",
        ".nuxt", ".svelte-kit", ".turbo", ".parcel-cache", "coverage", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox",
        ".vs", ".idea", ".cache", "packages",
    };

    private readonly ConditionalWeakTable<UninstallPlan, PlanContext> _issuedPlans = [];
    private readonly AuditLog _audit = new(options.AuditLogPath);

    public async Task<UninstallPlan> PlanAsync(string itemId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        var item = catalog.State.Items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new KeyNotFoundException(L.T($"Öğe bulunamadı: {itemId}", $"Item not found: {itemId}"));
        var recycle = catalog.State.Settings.UseRecycleBin;
        var steps = new List<UninstallStep>();
        var warnings = new List<PlanWarning>();
        string? uninstallerUnderLocation = null;

        switch (item.Kind)
        {
            case ItemKind.GitRepository:
            case ItemKind.ProjectFolder:
                await PlanFolderAsync(item, recycle, steps, warnings, ct).ConfigureAwait(false);
                break;
            case ItemKind.InstalledApp:
                uninstallerUnderLocation = PlanInstalledApp(item, recycle, steps, warnings, ct);
                break;
            case ItemKind.PackageTool:
                PlanPackageTool(item, recycle, steps, warnings);
                break;
            default:
                warnings.Add(new PlanWarning(WarningLevel.Blocker, L.T("Bu öğe türü kaldırılamaz.", "This kind of item cannot be uninstalled.")));
                break;
        }

        steps.Add(new UninstallStep
        {
            Kind = UninstallStepKind.RemoveCatalogEntry,
            Description = L.T("Tezgah listesinden çıkar", "Remove from the Tezgah list"),
        });

        // Immutable snapshot: the lists handed out cannot be cast back and edited, and execution only uses the copy below.
        var frozenSteps = steps.ToImmutableArray();
        var frozenWarnings = warnings.ToImmutableArray();
        var plan = new UninstallPlan { ItemId = item.Id, ItemName = item.Name, Steps = frozenSteps, Warnings = frozenWarnings };
        _issuedPlans.AddOrUpdate(plan, new PlanContext(
            item.Kind,
            item.Path,
            item.WebUrl,
            recycle,
            item.Id,
            item.Name,
            frozenSteps,
            frozenWarnings.Any(w => w.Level == WarningLevel.Blocker),
            frozenWarnings.Any(w => w.Level == WarningLevel.Danger),
            frozenWarnings.Where(w => w.Level == WarningLevel.Danger).Select(w => w.Message).ToImmutableHashSet(StringComparer.Ordinal),
            uninstallerUnderLocation));
        return plan;
    }

    public async Task<UninstallResult> ExecuteAsync(UninstallPlan plan, IReadOnlyCollection<UninstallStep> selectedSteps, string? typedConfirmation, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selectedSteps);

        if (!_issuedPlans.TryGetValue(plan, out var context))
        {
            throw new InvalidOperationException(L.T(
                "Bu plan Tezgah tarafından oluşturulmamış ya da değiştirilmiş; yeniden plan oluşturun.",
                "This plan was not produced by Tezgah or has been modified; create a new plan."));
        }

        if (context.Blocked)
        {
            throw new InvalidOperationException(L.T("Plan engellendi; kaldırma yapılamaz.", "The plan is blocked; nothing can be removed."));
        }

        if (context.RequiresTypedConfirmation
            && !string.Equals(typedConfirmation?.Trim(), context.ItemName.Trim(), StringComparison.Ordinal))
        {
            throw new UninstallConfirmationException();
        }

        // The dialog may have been open for a long time: re-read the folder's state before anything is touched.
        if (context.Kind is ItemKind.GitRepository or ItemKind.ProjectFolder && context.ItemPath is not null && Directory.Exists(context.ItemPath))
        {
            var fresh = await ComputeFolderSafetyWarningsAsync(context.Kind, Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.ItemPath)), context.WebUrl, ct).ConfigureAwait(false);
            if (fresh.Any(w => w.Level == WarningLevel.Danger && !context.DangerMessages.Contains(w.Message)))
            {
                _issuedPlans.Remove(plan);
                throw new UninstallConfirmationException(L.T(
                    "Plan oluşturulduktan sonra klasörde kaydedilmemiş iş oluştu; plan yeniden oluşturuldu, lütfen tekrar gözden geçirin.",
                    "Unsaved work appeared in the folder after the plan was made; the plan was refreshed, please review it again."))
                { PlanChanged = true };
            }
        }

        // Only one execution per plan: a second call must re-plan (state may have changed).
        _issuedPlans.Remove(plan);

        var toRun = context.Steps.Where(s => !s.Optional || selectedSteps.Contains(s)).ToList();
        var runId = Guid.NewGuid().ToString("N");

        // Intent first: if the audit log cannot be written, nothing is deleted.
        var started = await _audit.TryWriteAsync(new AuditEntry(
            DateTimeOffset.Now, "uninstall", context.ItemId, context.ItemName, "started",
            toRun.Select(s => new AuditStep(s.Kind, s.Path, s.Command?.Display, false, null)).ToList(), null, runId), CancellationToken.None).ConfigureAwait(false);
        if (started is not null)
        {
            throw new InvalidOperationException(L.T(
                $"Denetim kaydı yazılamadı; hiçbir şey silinmedi: {started}",
                $"The audit log could not be written; nothing was removed: {started}"));
        }

        var outcomes = new List<StepOutcome>();
        var outcome = "success";
        string? fatal = null;
        string? auditWarning = null;
        try
        {
            foreach (var step in toRun)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(step.Description);
                StepOutcome result;
                try
                {
                    await ExecuteStepAsync(context, step, progress, ct).ConfigureAwait(false);
                    result = new StepOutcome(step, true, null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = new StepOutcome(step, false, ex.Message);
                }

                outcomes.Add(result);
                if (!result.Success)
                {
                    progress?.Report(L.T($"Başarısız: {step.Description} — {result.Error}", $"Failed: {step.Description} — {result.Error}"));
                    if (!step.Optional)
                    {
                        outcome = "failed";
                        break;
                    }

                    outcome = "partial";
                }
            }

            if (outcome != "failed")
            {
                progress?.Report(L.T("Kaldırma tamamlandı.", "Uninstall finished."));
            }
        }
        catch (OperationCanceledException ex)
        {
            outcome = "cancelled";
            fatal = ex.Message;
            throw;
        }
        finally
        {
            var entry = new AuditEntry(
                DateTimeOffset.Now,
                "uninstall",
                context.ItemId,
                context.ItemName,
                outcome,
                outcomes.Select(o => new AuditStep(o.Step.Kind, o.Step.Path, o.Step.Command?.Display, o.Success, o.Error)).ToList(),
                fatal,
                runId);

            // Never let a failing audit write replace the real outcome (the work is already done).
            if (await _audit.TryWriteAsync(entry, CancellationToken.None).ConfigureAwait(false) is { } error)
            {
                auditWarning = L.T($"Kaldırma yapıldı ama denetim kaydı yazılamadı: {error}", $"Uninstalled, but the audit log could not be written: {error}");
                progress?.Report(auditWarning);
            }
        }

        return new UninstallResult(outcomes) { AuditWarning = auditWarning };
    }

    // ───────────────────────────── planning ─────────────────────────────

    private async Task PlanFolderAsync(CatalogItem item, bool recycle, List<UninstallStep> steps, List<PlanWarning> warnings, CancellationToken ct)
    {
        if (item.Path is null)
        {
            warnings.Add(new PlanWarning(WarningLevel.Blocker, L.T("Öğenin klasörü bilinmiyor.", "The item's folder is unknown.")));
            return;
        }

        var check = guard.Check(item.Path, PathOperation.Delete);
        if (!check.Allowed)
        {
            warnings.Add(new PlanWarning(WarningLevel.Blocker, check.Reason ?? L.T("Bu klasör silinemez.", "This folder cannot be deleted.")));
            return;
        }

        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path));
        warnings.AddRange(await ComputeFolderSafetyWarningsAsync(item.Kind, path, item.WebUrl, ct).ConfigureAwait(false));

        if (item.Kind == ItemKind.GitRepository)
        {
            await AddIgnoredFileWarningsAsync(path, recycle, warnings, ct).ConfigureAwait(false);
        }

        AddStopProcessesStep(item.Id, path, steps, warnings);

        var size = item.SizeBytes ?? DirectorySize.Measure(path, ct);
        steps.Add(new UninstallStep
        {
            Kind = UninstallStepKind.RemoveDirectory,
            Path = path,
            SizeBytes = size,
            Description = DeleteDescription(path, recycle),
        });
        warnings.Add(new PlanWarning(WarningLevel.Info, L.T($"{FormatBytes(size)} yer açılacak.", $"{FormatBytes(size)} will be freed.")));
        if (!recycle)
        {
            warnings.Add(new PlanWarning(WarningLevel.Warning, L.T("Silme kalıcı olacak (Geri Dönüşüm Kutusu kapalı).", "Deletion is permanent (Recycle Bin is turned off).")));
        }

        foreach (var (shortcut, target) in FindShortcutsInto(path, ct))
        {
            steps.Add(new UninstallStep
            {
                Kind = UninstallStepKind.RemoveShortcut,
                Path = shortcut,
                Optional = true,
                Description = L.T($"Kısayolu kaldır: {Path.GetFileName(shortcut)} → {target}", $"Remove shortcut: {Path.GetFileName(shortcut)} → {target}"),
            });
        }

        var uncertainVenvs = new List<string>();
        foreach (var (venv, exact) in FindRelatedVirtualenvs(item, path))
        {
            if (!guard.Check(venv, PathOperation.Delete).Allowed)
            {
                continue;
            }

            var venvSize = DirectorySize.Measure(venv, ct);
            steps.Add(new UninstallStep
            {
                Kind = UninstallStepKind.RemoveDirectory,
                Path = venv,
                SizeBytes = venvSize,
                Optional = true,
                DefaultSelected = exact,
                Description = L.T(
                    $"İlgili sanal ortamı da kaldır ({FormatBytes(venvSize)}): {venv}",
                    $"Also remove the related virtual environment ({FormatBytes(venvSize)}): {venv}"),
            });
            if (!exact)
            {
                uncertainVenvs.Add(Path.GetFileName(venv));
            }
        }

        if (uncertainVenvs.Count > 0)
        {
            warnings.Add(new PlanWarning(WarningLevel.Warning, L.T(
                $"Şu sanal ortamların bu projeye ait olduğu yalnızca addan tahmin edildi (seçili değil): {string.Join(", ", uncertainVenvs)}",
                $"These virtual environments only match by name; ownership could not be verified (not selected): {string.Join(", ", uncertainVenvs)}")));
        }
    }

    /// <summary>
    /// Warnings that decide whether deleting a project folder needs typed confirmation: the folder's own git state (or the
    /// lack of version control) plus every nested repository inside it. Used at planning and again right before execution.
    /// </summary>
    private async Task<List<PlanWarning>> ComputeFolderSafetyWarningsAsync(ItemKind kind, string path, string? webUrl, CancellationToken ct)
    {
        var warnings = new List<PlanWarning>();
        var nested = FindNestedRepositories(path, ct, out var incomplete);

        if (kind == ItemKind.GitRepository)
        {
            AddGitWarnings(await ReadGitAsync(path, ct).ConfigureAwait(false), warnings, label: null);
            AddSubmoduleGitDirWarnings(path, path, nested, warnings, label: null);
        }
        else if (nested.Count > 0)
        {
            warnings.Add(new PlanWarning(WarningLevel.Warning, L.T(
                $"Bu klasörün kendisi sürüm kontrolünde değil ama içinde {nested.Count} git deposu var.",
                $"This folder itself is not under version control but it contains {nested.Count} git repositor(y/ies).")));
        }
        else if (string.IsNullOrWhiteSpace(webUrl))
        {
            warnings.Add(new PlanWarning(WarningLevel.Danger, L.T(
                "Bu klasör sürüm kontrolünde değil ve bilinen bir kaynağı yok — büyük olasılıkla tek kopya bu.",
                "This folder is not under version control and has no known source — this is probably the only copy.")));
        }
        else
        {
            warnings.Add(new PlanWarning(WarningLevel.Warning, L.T(
                $"Git yok; yeniden indirilebilir ({webUrl}) ama yerel değişiklikler kaybolur.",
                $"No git; it can be downloaded again ({webUrl}) but local changes will be lost.")));
        }

        var clean = new List<string>();
        foreach (var repo in nested.Take(MaxNestedRepositories))
        {
            var relative = Path.GetRelativePath(path, repo);
            var info = await ReadGitAsync(repo, ct).ConfigureAwait(false);
            var before = warnings.Count;
            AddGitWarnings(info, warnings, relative);
            AddSubmoduleGitDirWarnings(repo, path, nested, warnings, relative);
            if (warnings.Count == before)
            {
                clean.Add(relative);
            }
        }

        if (clean.Count > 0)
        {
            warnings.Add(new PlanWarning(WarningLevel.Info, L.T(
                $"İç içe depolar da silinecek: {string.Join(", ", clean.Take(5))}{(clean.Count > 5 ? " …" : string.Empty)}",
                $"Nested repositories will be deleted too: {string.Join(", ", clean.Take(5))}{(clean.Count > 5 ? " …" : string.Empty)}")));
        }

        if (incomplete || nested.Count > MaxNestedRepositories)
        {
            warnings.Add(new PlanWarning(WarningLevel.Danger, L.T(
                "Klasörün tamamı iç içe git depoları için taranamadı; kaydedilmemiş iş olup olmadığı doğrulanamadı.",
                "Not every subfolder could be checked for nested git repositories; unsaved work could not be ruled out.")));
        }

        return warnings;
    }

    private const int MaxNestedRepositories = 50;

    /// <summary>
    /// Submodule repositories kept in <c>.git/modules</c> are deleted with the folder. Neither the parent's git state nor the
    /// nested-repository walk sees them once the submodule was deinitialised or removed (no working tree left), yet they
    /// may hold the only copy of commits never pushed. Each one not already inspected as a checked-out nested repository
    /// is checked from its refs; anything unproven, and a modules folder that cannot be enumerated, is Danger.
    /// Only git data that the deletion actually removes is checked: a linked worktree (or a nested repository that is a
    /// linked worktree of a repository elsewhere) keeps its common git directory, modules included, outside
    /// <paramref name="deletedRoot"/>, so those submodule repositories survive and are not reported.
    /// </summary>
    private static void AddSubmoduleGitDirWarnings(string repo, string deletedRoot, IReadOnlyList<string> nested, List<PlanWarning> warnings, string? label)
    {
        if (!GitRepoGuard.TryLocateGitDirs(repo, out _, out var commonDir, out _))
        {
            return; // the repository's own git state is already unreadable, which is Danger
        }

        if (!PathGuard.IsSameOrUnder(Path.GetFullPath(commonDir!), Path.GetFullPath(deletedRoot)))
        {
            return; // .git/modules lives in a repository outside the deleted folder and is kept
        }

        string Prefix(string tr, string en) => label is null ? L.T(tr, en) : L.T($"İç içe depo '{label}': {tr}", $"Nested repository '{label}': {en}");

        // Git directories of submodules checked out as nested repositories: those are inspected as repositories themselves.
        var inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nested)
        {
            if (GitRepoGuard.TryLocateGitDirs(n, out var g, out _, out _))
            {
                inspected.Add(Path.TrimEndingDirectorySeparator(g!));
            }
        }

        var modules = GitMetadataReader.ReadSubmoduleGitDirs(commonDir!, inspected);
        if (modules is null)
        {
            warnings.Add(new PlanWarning(WarningLevel.Danger, Prefix(
                "Alt modül verisi (.git/modules) denetlenemedi; gönderilmemiş commit olup olmadığı doğrulanamadı.",
                "Submodule data in .git/modules could not be checked; unpushed commits could not be ruled out.")));
            return;
        }

        foreach (var m in modules.Where(m => m.Problem != GitMetadataReader.ModuleProblem.None).Take(10))
        {
            var (tr, en) = m.Problem switch
            {
                GitMetadataReader.ModuleProblem.NoRemote => ("uzak deposu yok, tek kopyası burada", "it has no remote, this is its only copy"),
                GitMetadataReader.ModuleProblem.Stash => ("stash içeriyor", "it holds stashed changes"),
                GitMetadataReader.ModuleProblem.UnpushedBranches => (
                    $"hiçbir uzakta olmayan commit'ler içeriyor ({string.Join(", ", m.Branches)})",
                    $"it holds commits no remote has ({string.Join(", ", m.Branches)})"),
                GitMetadataReader.ModuleProblem.UnpushedHead => ("HEAD commit'inin uzakta olduğu doğrulanamadı", "its HEAD commit could not be shown to be on a remote"),
                _ => ("okunamadı; gönderilmemiş commit olup olmadığı doğrulanamadı", "it could not be read; unpushed commits could not be ruled out"),
            };
            warnings.Add(new PlanWarning(WarningLevel.Danger, Prefix(
                $"Alt modül deposu .git/modules/{m.Name} (çalışma ağacı yok ya da ayrıca denetlenmedi) silinecek: {tr}.",
                $"Submodule repository .git/modules/{m.Name} (removed, deinitialised or not inspected separately) will be deleted: {en}.")));
        }
    }

    private async Task<GitInfo?> ReadGitAsync(string path, CancellationToken ct)
    {
        try
        {
            return await git.GetInfoAsync(path, fetch: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Git repositories strictly inside <paramref name="root"/> (a <c>.git</c> directory or file), including ignored ones git
    /// status never mentions, plus catalog repositories under it. Links are never followed; the walk is bounded.
    /// </summary>
    /// <remarks>
    /// Uses the same link test as <see cref="FileRemover"/> (<paramref name="isLinkLike"/>, default
    /// <see cref="FileRemover.IsLinkLike"/>): benign reparse points such as OneDrive placeholders are ordinary folders that the
    /// deletion recurses into, so they are searched too; only real links (whose targets the deletion never touches) are
    /// skipped. Folders that cannot be listed make the result incomplete (fail closed).
    /// </remarks>
    internal List<string> FindNestedRepositories(string root, CancellationToken ct, out bool incomplete, Func<FileSystemInfo, bool>? isLinkLike = null)
    {
        const int maxDirectories = 20_000;
        const int maxDepth = 8;
        isLinkLike ??= FileRemover.IsLinkLike;
        incomplete = false;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in catalog.State.Items)
        {
            if (i.Kind == ItemKind.GitRepository && i.Path is { } p && Path.IsPathFullyQualified(p)
                && PathGuard.IsStrictlyUnder(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)), root) && Directory.Exists(p))
            {
                found.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)));
            }
        }

        var options = WalkOptions;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        var visited = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            if (++visited > maxDirectories)
            {
                incomplete = true;
                break;
            }

            List<DirectoryInfo> children;
            try
            {
                children = new DirectoryInfo(dir).EnumerateDirectories("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                incomplete = true; // the deletion would still remove whatever is in there
                continue;
            }

            foreach (var info in children)
            {
                var child = info.FullName;
                var name = info.Name;
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool link;
                try
                {
                    link = isLinkLike(info);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    incomplete = true;
                    continue;
                }

                if (link)
                {
                    continue; // links are removed as links: their targets are never deleted
                }

                if (ProjectInspector.IsGitRoot(child))
                {
                    found.Add(child);
                    continue; // its own nested repos are its own business: it is reported as a whole
                }

                if (depth + 1 < maxDepth)
                {
                    stack.Push((child, depth + 1));
                }
                else
                {
                    incomplete = true;
                }
            }
        }

        return found.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddGitWarnings(GitInfo? info, List<PlanWarning> warnings, string? label)
    {
        string Prefix(string tr, string en) => label is null
            ? L.T(tr, en)
            : L.T($"İç içe depo '{label}': {tr}", $"Nested repository '{label}': {en}");

        if (info is null)
        {
            warnings.Add(new PlanWarning(WarningLevel.Danger, Prefix(
                "Git durumu okunamadı; kaydedilmemiş iş olup olmadığı doğrulanamadı.",
                "Could not read the git state; unsaved work could not be ruled out.")));
            return;
        }

        if (string.IsNullOrWhiteSpace(info.RemoteUrl))
        {
            warnings.Add(new PlanWarning(WarningLevel.Danger, Prefix(
                "Uzak depo (remote) yok — bu deponun tek kopyası bu klasör.",
                "No remote — this folder is the only copy of the repository.")));
        }

        if (info.HasUnsavedWork)
        {
            var parts = new List<string>();
            if (info.ModifiedFiles > 0)
            {
                parts.Add(L.T($"{info.ModifiedFiles} değişmiş dosya", $"{info.ModifiedFiles} modified file(s)"));
            }

            if (info.UntrackedFiles > 0)
            {
                parts.Add(L.T($"{info.UntrackedFiles} izlenmeyen dosya", $"{info.UntrackedFiles} untracked file(s)"));
            }

            if (info.Stashes > 0)
            {
                parts.Add(L.T($"{info.Stashes} stash", $"{info.Stashes} stash(es)"));
            }

            if (info.Ahead > 0)
            {
                parts.Add(L.T($"{info.Ahead} gönderilmemiş commit", $"{info.Ahead} unpushed commit(s)"));
            }

            if (!info.HasUpstream && info.HasCommits)
            {
                parts.Add(L.T(
                    $"'{info.Branch ?? "?"}' dalının uzakta karşılığı yok",
                    $"branch '{info.Branch ?? "?"}' has no upstream"));
            }
            else if (info.UpstreamGone && info.HasCommits)
            {
                parts.Add(L.T(
                    $"'{info.Branch ?? "?"}' dalının uzaktaki karşılığı silinmiş",
                    $"the upstream of branch '{info.Branch ?? "?"}' was deleted"));
            }

            if (info.StatusUnknown)
            {
                parts.Add(L.T(
                    "çalışma ağacındaki değişiklikler denetlenemedi (git bu depoda güvenle çalıştırılamadı)",
                    "changes in the working tree could not be checked (git could not be run safely on this repository)"));
            }

            if (info.UnpushedCommits is null)
            {
                parts.Add(L.T("diğer dallarda gönderilmemiş commit olup olmadığı doğrulanamadı", "could not rule out unpushed commits on other branches"));
            }
            else if (info.UnpushedCommits > info.Ahead)
            {
                var names = info.UnpushedBranches.Count > 0 ? " (" + string.Join(", ", info.UnpushedBranches.Take(5)) + ")" : string.Empty;
                parts.Add(L.T(
                    $"yerel dallarda hiçbir uzakta olmayan {info.UnpushedCommits} commit{names}",
                    $"{info.UnpushedCommits} commit(s) on local branches that no remote has{names}"));
            }

            if (info.LinkedWorktrees.Count > 0)
            {
                parts.Add(L.T(
                    $"{info.LinkedWorktrees.Count} bağlı çalışma ağacı bozulur: {string.Join(", ", info.LinkedWorktrees.Take(3))}",
                    $"{info.LinkedWorktrees.Count} linked worktree(s) break: {string.Join(", ", info.LinkedWorktrees.Take(3))}"));
            }

            warnings.Add(new PlanWarning(WarningLevel.Danger, Prefix(
                $"Kaydedilmemiş iş kaybolacak: {string.Join(", ", parts)}.",
                $"Unsaved work will be lost: {string.Join(", ", parts)}.")));
        }
    }

    /// <summary>
    /// Gitignored files are invisible to <c>git status</c> but may exist only here (.env secrets, local databases, keys).
    /// Regenerable folders are skipped. Sensitive files make the plan Danger when the Recycle Bin is off.
    /// </summary>
    private async Task AddIgnoredFileWarningsAsync(string path, bool recycle, List<PlanWarning> warnings, CancellationToken ct)
    {
        IReadOnlyList<string> ignored;
        try
        {
            ignored = await git.GetIgnoredPathsAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        var (sensitive, other) = ClassifyIgnored(ignored);
        if (sensitive.Count > 0)
        {
            var names = string.Join(", ", sensitive.Take(5)) + (sensitive.Count > 5 ? $" (+{sensitive.Count - 5})" : string.Empty);
            warnings.Add(new PlanWarning(recycle ? WarningLevel.Warning : WarningLevel.Danger, L.T(
                $"Git'te olmayan (yok sayılan) önemli yerel dosyalar da silinecek: {names}",
                $"Ignored local files that are not in git will be deleted too: {names}")));
        }

        if (other.Count > 0)
        {
            warnings.Add(new PlanWarning(WarningLevel.Info, L.T(
                $"{other.Count} yok sayılan (gitignore) yerel öğe yalnızca bu bilgisayarda var: {string.Join(", ", other.Take(3))}",
                $"{other.Count} gitignored local item(s) exist only on this PC: {string.Join(", ", other.Take(3))}")));
        }
    }

    /// <summary>Splits ignored paths into likely-sensitive files and other non-regenerable entries.</summary>
    internal static (List<string> Sensitive, List<string> Other) ClassifyIgnored(IEnumerable<string> ignored)
    {
        var sensitive = new List<string>();
        var other = new List<string>();
        foreach (var raw in ignored)
        {
            var entry = raw.Replace('\\', '/').TrimEnd('/');
            if (entry.Length == 0)
            {
                continue;
            }

            var segments = entry.Split('/');
            if (segments.Any(s => RegenerableDirectories.Contains(s)) || segments[^1].EndsWith(".pyc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            (IsSensitiveName(segments[^1]) ? sensitive : other).Add(entry);
        }

        return (sensitive, other);
    }

    private static bool IsSensitiveName(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower is ".env.example" or ".env.sample" or ".env.template" or ".env.dist")
        {
            return false;
        }

        return lower == ".env" || lower.StartsWith(".env.", StringComparison.Ordinal) || lower.StartsWith("id_rsa", StringComparison.Ordinal)
            || lower is "secrets.json" or ".npmrc" or ".pypirc" or "credentials.json"
            || lower.EndsWith(".pem", StringComparison.Ordinal) || lower.EndsWith(".key", StringComparison.Ordinal)
            || lower.EndsWith(".pfx", StringComparison.Ordinal) || lower.EndsWith(".p12", StringComparison.Ordinal)
            || lower.EndsWith(".kdbx", StringComparison.Ordinal) || lower.EndsWith(".sqlite", StringComparison.Ordinal)
            || lower.EndsWith(".sqlite3", StringComparison.Ordinal) || lower.EndsWith(".db", StringComparison.Ordinal)
            || lower.EndsWith(".mdf", StringComparison.Ordinal);
    }

    /// <summary>Plans an installed-app removal. Returns the uninstaller executable when it lives inside the install folder.</summary>
    private string? PlanInstalledApp(CatalogItem item, bool recycle, List<UninstallStep> steps, List<PlanWarning> warnings, CancellationToken ct)
    {
        if (item.UninstallCommand is null)
        {
            warnings.Add(new PlanWarning(WarningLevel.Blocker, L.T(
                "Bu uygulamanın kaldırıcısı bulunamadı. Windows Ayarlar › Uygulamalar'ı kullanın.",
                "No uninstaller was found for this app. Use Windows Settings › Apps.")));
            return null;
        }

        var location = item.Path is not null && Path.IsPathFullyQualified(item.Path)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path))
            : null;
        string? uninstallerUnder = null;
        if (location is not null && Path.IsPathFullyQualified(item.UninstallCommand.FileName)
            && PathGuard.IsSameOrUnder(Path.GetFullPath(item.UninstallCommand.FileName), location))
        {
            uninstallerUnder = Path.GetFullPath(item.UninstallCommand.FileName);
        }

        string? refusal = null;
        if (location is not null)
        {
            var check = guard.Check(location, PathOperation.Delete, DeletePolicy.InstalledAppLeftovers);
            refusal = check.Allowed ? LeftoverRefusal(item, location, uninstallerUnder is not null, ct) : check.Reason;
        }

        var owned = location is not null && refusal is null;
        if (owned)
        {
            AddStopProcessesStep(item.Id, location!, steps, warnings);
        }

        steps.Add(new UninstallStep
        {
            Kind = UninstallStepKind.RunCommand,
            Command = item.UninstallCommand with { WaitForProcessTree = true },
            Description = L.T($"Uygulamanın kendi kaldırıcısını çalıştır: {item.UninstallCommand.Display}", $"Run the app's own uninstaller: {item.UninstallCommand.Display}"),
        });
        warnings.Add(new PlanWarning(WarningLevel.Info, L.T(
            "Üreticinin kaldırıcısı kendi penceresini açabilir.",
            "The vendor uninstaller may show its own window.")));

        if (owned)
        {
            var size = FsUtil.DirectorySize(location!, maxEntries: 200_000, budget: TimeSpan.FromSeconds(4), ct: ct);
            steps.Add(new UninstallStep
            {
                Kind = UninstallStepKind.RemoveDirectory,
                Path = location,
                SizeBytes = size,
                Optional = true,
                DefaultSelected = false,
                Description = L.T(
                    $"Kaldırıcıdan sonra kalan dosyaları sil (varsa, şu an {FormatBytes(size)}): {location}",
                    $"Delete leftover files after the uninstaller (if any, currently {FormatBytes(size)}): {location}") + " — " + DeleteDescription(location!, recycle),
            });
            warnings.Add(new PlanWarning(WarningLevel.Info, L.T(
                "Kalan dosyaların silinmesi seçili değil; kaldırıcı bitince klasör yeniden denetlenir, veri içeriyorsa dokunulmaz.",
                "Deleting leftover files is not selected; the folder is re-checked after the uninstaller and left alone if it holds data.")));
            if (!recycle)
            {
                warnings.Add(new PlanWarning(WarningLevel.Danger, L.T(
                    "Geri Dönüşüm Kutusu kapalı: kalan dosyalar seçilirse kalıcı olarak silinir.",
                    "The Recycle Bin is turned off: leftover files are deleted permanently if selected.")));
            }
        }
        else if (location is not null)
        {
            warnings.Add(new PlanWarning(WarningLevel.Warning, L.T(
                $"Kurulum klasörüne dokunulmayacak ({refusal}): {location}",
                $"The install folder will not be touched ({refusal}): {location}")));
        }

        return uninstallerUnder;
    }

    /// <summary>
    /// Why <paramref name="location"/> must not be swept as leftovers of <paramref name="item"/> (null = it may be offered).
    /// Registry InstallLocation is vendor-supplied and per-user installers can write anything there.
    /// </summary>
    private string? LeftoverRefusal(CatalogItem item, string location, bool uninstallerInside, CancellationToken ct)
    {
        for (var dir = location; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (Path.Exists(Path.Combine(dir, ".git")))
            {
                return L.T("klasör bir git deposunun içinde", "the folder is inside a git repository");
            }
        }

        foreach (var other in catalog.State.Items)
        {
            if (other.Id == item.Id)
            {
                continue;
            }

            if (other.Path is { } p && Path.IsPathFullyQualified(p))
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
                if (PathGuard.IsSameOrUnder(full, location) || PathGuard.IsSameOrUnder(location, full))
                {
                    return L.T($"başka bir öğeyle paylaşılıyor: {other.Name}", $"shared with another item: {other.Name}");
                }
            }

            if (other.Kind == ItemKind.InstalledApp && other.UninstallCommand?.FileName is { } exe && Path.IsPathFullyQualified(exe)
                && PathGuard.IsSameOrUnder(Path.GetFullPath(exe), location))
            {
                return L.T($"başka bir programın kaldırıcısı burada: {other.Name}", $"another program's uninstaller lives here: {other.Name}");
            }
        }

        // Ownership evidence: the app's own uninstaller lives in the folder, or the folder is a vendor folder under an
        // install root / the per-user Programs folder (never an arbitrary user folder).
        var perUserPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        var underRoot = guard.InstallRoots.Any(r => PathGuard.IsStrictlyUnder(location, r)) || PathGuard.IsStrictlyUnder(location, perUserPrograms);
        if (!uninstallerInside && !underRoot)
        {
            return L.T("uygulamaya ait olduğu doğrulanamadı", "it could not be verified to belong to the app");
        }

        if (FindDataMarker(location, ct) is { } marker)
        {
            return L.T($"kullanıcı verisi içerebilir: {marker}", $"it may hold user data: {marker}");
        }

        return null;
    }

    /// <summary>Directory walk options shared by the safety scans: nothing skipped by attribute, link decisions are explicit.</summary>
    private static EnumerationOptions WalkOptions => new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };

    /// <summary>
    /// First sign of user data (databases, repositories, backups) inside <paramref name="folder"/>, bounded walk. When the walk
    /// could not look everywhere (bounds hit, unreadable folder) and found nothing, the result is a "not fully checked"
    /// description instead of null: callers must not treat an incomplete search as "no user data" (fail closed). Benign
    /// reparse points (cloud placeholders) are searched like ordinary folders, as the deletion recurses into them too.
    /// </summary>
    internal static string? FindDataMarker(string folder, CancellationToken ct, Func<FileSystemInfo, bool>? isLinkLike = null)
    {
        const int maxDirectories = 5_000;
        const int maxDepth = 6;
        isLinkLike ??= FileRemover.IsLinkLike;
        var options = WalkOptions;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((folder, 0));
        var visited = 0;
        var incomplete = false;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (++visited > maxDirectories)
            {
                incomplete = true;
                break;
            }

            var (dir, depth) = stack.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    if (entry is DirectoryInfo sub)
                    {
                        if (DataMarkerDirectories.Contains(sub.Name))
                        {
                            return sub.FullName;
                        }

                        if (isLinkLike(sub))
                        {
                            continue; // removed as a link, its target is never touched
                        }

                        if (depth < maxDepth)
                        {
                            stack.Push((sub.FullName, depth + 1));
                        }
                        else
                        {
                            incomplete = true;
                        }
                    }
                    else if (DataMarkerFiles.Contains(entry.Name) || DataMarkerExtensions.Any(e => entry.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    {
                        return entry.FullName;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                incomplete = true;
            }
        }

        return incomplete
            ? L.T($"{folder} tamamen denetlenemedi (çok büyük, çok derin ya da okunamayan klasörler var)", $"{folder} could not be fully checked (too large, too deep or unreadable folders)")
            : null;
    }

    private void PlanPackageTool(CatalogItem item, bool recycle, List<UninstallStep> steps, List<PlanWarning> warnings)
    {
        if (item.UninstallCommand is not null)
        {
            steps.Add(new UninstallStep
            {
                Kind = UninstallStepKind.RunCommand,
                Command = item.UninstallCommand,
                Description = L.T($"Paket yöneticisiyle kaldır: {item.UninstallCommand.Display}", $"Remove with the package manager: {item.UninstallCommand.Display}"),
            });
            return;
        }

        // `go install` binaries have no uninstaller: the tool is a single file. Contract gap: there is no RemoveFile step
        // kind, so a RemoveDirectory step whose Path is a file is used and executed as a file deletion.
        if (item.Source == Sources.GoBin && item.Path is not null && File.Exists(item.Path) && !Directory.Exists(item.Path))
        {
            var check = guard.Check(item.Path, PathOperation.Delete);
            if (!check.Allowed)
            {
                warnings.Add(new PlanWarning(WarningLevel.Blocker, check.Reason ?? string.Empty));
                return;
            }

            var file = Path.GetFullPath(item.Path);
            steps.Add(new UninstallStep
            {
                Kind = UninstallStepKind.RemoveDirectory,
                Path = file,
                SizeBytes = new FileInfo(file).Length,
                Description = L.T($"Programı sil: {file}", $"Delete the program: {file}") + " — " + DeleteDescription(file, recycle),
            });
            return;
        }

        warnings.Add(new PlanWarning(WarningLevel.Blocker, L.T("Bu aracı kaldırmanın bir yolu bulunamadı.", "No way to remove this tool was found.")));
    }

    private void AddStopProcessesStep(string itemId, string path, List<UninstallStep> steps, List<PlanWarning> warnings)
    {
        var tracked = runService.ForItem(itemId).Count(r => !r.HasExited);
        var external = processFinder.FindUnder(path);
        var total = tracked + external.Count;
        steps.Add(new UninstallStep
        {
            Kind = UninstallStepKind.StopProcesses,
            Path = path,
            Description = L.T($"Bu klasörden çalışan işlemleri durdur ({total})", $"Stop processes running from this folder ({total})"),
        });
        if (external.Count > 0)
        {
            var names = string.Join(", ", external.Take(5).Select(p => $"{Path.GetFileName(p.ImagePath)} ({p.ProcessId.ToString(CultureInfo.InvariantCulture)})"))
                + (external.Count > 5 ? " …" : string.Empty);
            warnings.Add(new PlanWarning(WarningLevel.Warning, L.T(
                $"Şu işlemler kapatılacak, içlerindeki kaydedilmemiş iş kaybolur: {names}",
                $"These processes will be closed; unsaved work in them is lost: {names}")));
        }
        else if (tracked > 0)
        {
            warnings.Add(new PlanWarning(WarningLevel.Info, L.T(
                $"{tracked} çalışan işlem durdurulacak.",
                $"{tracked} running process(es) will be stopped.")));
        }
    }

    /// <summary>
    /// Virtualenvs created outside the project. Exact (owner proven): pipenv/virtualenvwrapper <c>.project</c> files and
    /// Poetry cache folders whose path hash matches the project. Name-only matches are returned as not exact.
    /// </summary>
    private List<(string Dir, bool Exact)> FindRelatedVirtualenvs(CatalogItem item, string path)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in new[] { Path.GetFileName(path), item.Name })
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            names.Add(raw);
            names.Add(raw.Replace('_', '-').Replace('.', '-').Replace(' ', '-').ToLowerInvariant());
        }

        var poetryHash = PoetryPathHash(path);
        var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in options.VirtualenvRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var dirName = Path.GetFileName(dir);

                // pipenv / virtualenvwrapper record the owning project in <venv>\.project — exact match when present.
                var projectFile = Path.Combine(dir, ".project");
                if (File.Exists(projectFile))
                {
                    string owner;
                    try
                    {
                        owner = File.ReadAllText(projectFile).Trim();
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    if (Path.IsPathFullyQualified(owner)
                        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(owner)), path, StringComparison.OrdinalIgnoreCase))
                    {
                        found[dir] = true;
                    }

                    continue;
                }

                // Poetry: "<name>-<8 char hash of the project path>-py3.x"; the hash identifies the owner.
                if (PoetryVenvName().Match(dirName) is { Success: true } m)
                {
                    if (string.Equals(m.Groups["h"].Value, poetryHash, StringComparison.Ordinal))
                    {
                        found[dir] = true;
                    }

                    continue;
                }

                // Anything else (virtualenvwrapper without .project): exact name only, never a prefix, never pre-selected.
                if (names.Contains(dirName))
                {
                    found.TryAdd(dir, false);
                }
            }
        }

        return found.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// The 8-character hash Poetry puts in virtualenv names (EnvManager.generate_env_name):
    /// urlsafe-base64(sha256(normcase(realpath(project))))[:8]; normcase lower-cases on Windows.
    /// </summary>
    internal static string PoetryPathHash(string projectDirectory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory)).Replace('/', '\\').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_')[..8];
    }

    [GeneratedRegex(@"^(?<n>.+)-(?<h>[A-Za-z0-9_-]{8})-py\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex PoetryVenvName();

    private List<(string Shortcut, string Target)> FindShortcutsInto(string path, CancellationToken ct)
    {
        var result = new List<(string, string)>();
        var enumerated = 0;
        IEnumerable<string> Enumerate(string root, bool recursive)
        {
            if (!Directory.Exists(root))
            {
                return [];
            }

            return Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MaxRecursionDepth = 6,
            });
        }

        var root = CanonicalForComparison(path);
        var candidates = options.ShortcutRootsRecursive.SelectMany(r => Enumerate(r, true))
            .Concat(options.ShortcutRootsTopLevel.SelectMany(r => Enumerate(r, false)));
        foreach (var lnk in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (++enumerated > 5000)
            {
                break;
            }

            if (CanonicalShortcutTarget(ShortcutReader.TryGetTarget(lnk)) is { } target && PathGuard.IsSameOrUnder(target, root))
            {
                result.Add((lnk, target));
            }
        }

        return result;
    }

    /// <summary>
    /// Canonical form of a shortcut target for containment checks, or null when it cannot be compared safely:
    /// not fully qualified, UNC/device, or containing '.'/'..' segments (a crafted "proj\..\other" must not match "proj").
    /// 8.3 short names are expanded when the target exists.
    /// </summary>
    internal static string? CanonicalShortcutTarget(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Path.IsPathFullyQualified(raw) || raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        if (raw.Split('\\', '/').Any(s => s is "." or ".."))
        {
            return null;
        }

        return CanonicalForComparison(raw);
    }

    private static string CanonicalForComparison(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }

        return Path.Exists(full) && Win32.GetLongPath(full) is { } longPath ? Path.TrimEndingDirectorySeparator(longPath) : full;
    }

    // ───────────────────────────── execution ─────────────────────────────

    private async Task ExecuteStepAsync(PlanContext context, UninstallStep step, IProgress<string>? progress, CancellationToken ct)
    {
        var policy = context.Kind == ItemKind.InstalledApp ? DeletePolicy.InstalledAppLeftovers : DeletePolicy.Default;
        switch (step.Kind)
        {
            case UninstallStepKind.StopProcesses:
                await StopProcessesAsync(context.ItemId, step.Path, ct).ConfigureAwait(false);
                break;

            case UninstallStepKind.RunCommand:
                var command = step.Command ?? throw new InvalidOperationException("RunCommand step without command.");
                var result = await executor.RunAsync(command, options.CommandTimeout, ct).ConfigureAwait(false);
                if (result.TimedOut)
                {
                    throw new TimeoutException(L.T("Kaldırıcı zaman aşımına uğradı.", "The uninstaller timed out."));
                }

                if (!SuccessExitCodes.Contains(result.ExitCode))
                {
                    var detail = (result.StdErr.Length > 0 ? result.StdErr : result.StdOut).Trim();
                    if (detail.Length > 400)
                    {
                        detail = detail[..400] + "…";
                    }

                    throw new InvalidOperationException(L.T(
                        $"Kaldırıcı {result.ExitCode} koduyla bitti. {detail}",
                        $"The uninstaller exited with code {result.ExitCode}. {detail}"));
                }

                if (context.Kind == ItemKind.InstalledApp && context.UninstallerUnderLocation is { } uninstaller
                    && !await WaitUntilGoneAsync(uninstaller, ct).ConfigureAwait(false))
                {
                    // Exit code 0 from a stub (or a "Cancel" in the vendor UI) proves nothing: the uninstaller is still there.
                    throw new InvalidOperationException(L.T(
                        "Kaldırıcı bitti ama uygulama hâlâ kurulu görünüyor (kaldırıcısı yerinde); kalan dosyalar silinmedi. Windows Ayarlar › Uygulamalar'ı deneyin.",
                        "The uninstaller finished but the app still appears to be installed (its uninstaller is still there); leftovers were not deleted. Try Windows Settings › Apps."));
                }

                break;

            case UninstallStepKind.RemoveDirectory:
                var target = step.Path ?? throw new InvalidOperationException("RemoveDirectory step without path.");
                if (!Path.Exists(target))
                {
                    // e.g. the vendor uninstaller already removed its install folder — nothing left to do.
                    progress?.Report(L.T($"Zaten yok: {target}", $"Already gone: {target}"));
                    break;
                }

                if (context.Kind == ItemKind.InstalledApp && Directory.Exists(target))
                {
                    // Look again after the uninstaller: it may deliberately keep data (e.g. a PostgreSQL cluster).
                    if (FindDataMarker(target, ct) is { } marker)
                    {
                        throw new InvalidOperationException(L.T(
                            $"Kalan klasörde kullanıcı verisi olabilir ({marker}); dokunulmadı.",
                            $"The remaining folder may hold user data ({marker}); left untouched."));
                    }

                    var remaining = Directory.EnumerateFileSystemEntries(target, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(100_001).Count();
                    progress?.Report(L.T($"Kalan {remaining} öğe siliniyor: {target}", $"Deleting {remaining} remaining item(s): {target}"));
                }

                if (File.Exists(target))
                {
                    await remover.RemoveFileAsync(target, context.Recycle, policy, ct).ConfigureAwait(false);
                }
                else
                {
                    await remover.RemoveDirectoryAsync(target, context.Recycle, policy, ct).ConfigureAwait(false);
                }

                break;

            case UninstallStepKind.RemoveShortcut:
                var lnk = step.Path ?? throw new InvalidOperationException("RemoveShortcut step without path.");
                if (!File.Exists(lnk))
                {
                    break;
                }

                // Re-verify it is a shortcut that still points into the item before touching it.
                if (!lnk.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || context.ItemPath is null
                    || CanonicalShortcutTarget(ShortcutReader.TryGetTarget(lnk)) is not { } lnkTarget
                    || !PathGuard.IsSameOrUnder(lnkTarget, CanonicalForComparison(context.ItemPath)))
                {
                    throw new InvalidOperationException(L.T("Kısayol artık bu öğeyi göstermiyor; dokunulmadı.", "The shortcut no longer points to this item; left untouched."));
                }

                await remover.RemoveFileAsync(lnk, context.Recycle, DeletePolicy.Default, ct).ConfigureAwait(false);
                break;

            case UninstallStepKind.RemoveCatalogEntry:
                await catalog.RemoveItemAsync(context.ItemId, ct).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"Unknown step kind {step.Kind}.");
        }
    }

    /// <summary>Self-deleting uninstallers remove themselves shortly after exiting; give them a few seconds.</summary>
    private static async Task<bool> WaitUntilGoneAsync(string file, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            if (!File.Exists(file))
            {
                return true;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return !File.Exists(file);
    }

    private async Task StopProcessesAsync(string itemId, string? path, CancellationToken ct)
    {
        foreach (var run in runService.ForItem(itemId))
        {
            await runService.StopAsync(run.RunId, ct).ConfigureAwait(false);
        }

        if (path is null || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            return;
        }

        var failed = new List<string>();
        foreach (var process in processFinder.FindUnder(path))
        {
            if (!processFinder.TryKill(process, path))
            {
                failed.Add($"{Path.GetFileName(process.ImagePath)} ({process.ProcessId.ToString(CultureInfo.InvariantCulture)})");
            }
        }

        if (failed.Count > 0)
        {
            throw new InvalidOperationException(L.T(
                $"Bazı işlemler durdurulamadı: {string.Join(", ", failed)}",
                $"Some processes could not be stopped: {string.Join(", ", failed)}"));
        }
    }

    private static string DeleteDescription(string path, bool recycle) => recycle
        ? L.T($"Geri Dönüşüm Kutusu'na taşı: {path}", $"Move to the Recycle Bin: {path}")
        : L.T($"Kalıcı olarak sil: {path}", $"Delete permanently: {path}");

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    /// <summary>Everything execution needs, captured at planning time (the plan object handed out is never trusted).</summary>
    private sealed record PlanContext(
        ItemKind Kind,
        string? ItemPath,
        string? WebUrl,
        bool Recycle,
        string ItemId,
        string ItemName,
        ImmutableArray<UninstallStep> Steps,
        bool Blocked,
        bool RequiresTypedConfirmation,
        ImmutableHashSet<string> DangerMessages,
        string? UninstallerUnderLocation);
}
