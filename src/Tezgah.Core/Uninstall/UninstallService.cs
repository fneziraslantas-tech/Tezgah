using System.Globalization;
using System.Runtime.CompilerServices;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Execution;
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
}

/// <summary>
/// Builds and executes uninstall plans. Safety properties:
/// <list type="bullet">
/// <item>Only plans produced by <see cref="PlanAsync"/> of this instance are executed (a forged/modified plan is refused).</item>
/// <item>Blocked plans never run; Danger plans need the item name typed exactly.</item>
/// <item>The path guard is re-run immediately before every deletion (inside <see cref="FileRemover"/> as well).</item>
/// <item>Steps run sequentially and stop at the first failing mandatory step; the catalog entry is removed last.</item>
/// <item>Every execution is written to the audit log, including failures and cancellations.</item>
/// </list>
/// </summary>
internal sealed class UninstallService(
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

        switch (item.Kind)
        {
            case ItemKind.GitRepository:
            case ItemKind.ProjectFolder:
                await PlanFolderAsync(item, recycle, steps, warnings, ct).ConfigureAwait(false);
                break;
            case ItemKind.InstalledApp:
                PlanInstalledApp(item, recycle, steps, warnings);
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

        var plan = new UninstallPlan { ItemId = item.Id, ItemName = item.Name, Steps = steps, Warnings = warnings };
        _issuedPlans.AddOrUpdate(plan, new PlanContext(item.Kind, item.Path, recycle));
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

        if (plan.IsBlocked)
        {
            throw new InvalidOperationException(L.T("Plan engellendi; kaldırma yapılamaz.", "The plan is blocked; nothing can be removed."));
        }

        if (plan.RequiresTypedConfirmation
            && !string.Equals(typedConfirmation?.Trim(), plan.ItemName.Trim(), StringComparison.Ordinal))
        {
            throw new UninstallConfirmationException();
        }

        // Only one execution per plan: a second call must re-plan (state may have changed).
        _issuedPlans.Remove(plan);

        var toRun = plan.Steps.Where(s => !s.Optional || selectedSteps.Contains(s)).ToList();
        var outcomes = new List<StepOutcome>();
        var outcome = "success";
        string? fatal = null;
        try
        {
            foreach (var step in toRun)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(step.Description);
                StepOutcome result;
                try
                {
                    await ExecuteStepAsync(plan, context, step, progress, ct).ConfigureAwait(false);
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
                plan.ItemId,
                plan.ItemName,
                outcome,
                outcomes.Select(o => new AuditStep(o.Step.Kind, o.Step.Path, o.Step.Command?.Display, o.Success, o.Error)).ToList(),
                fatal);
            await _audit.WriteAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }

        return new UninstallResult(outcomes);
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

        // Fresh git state, local only (no network fetch).
        if (item.Kind == ItemKind.GitRepository)
        {
            GitInfo? info;
            try
            {
                info = await git.GetInfoAsync(path, fetch: false, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                info = null;
            }

            AddGitWarnings(info, warnings);
        }
        else if (string.IsNullOrWhiteSpace(item.WebUrl))
        {
            warnings.Add(new PlanWarning(WarningLevel.Danger, L.T(
                "Bu klasör sürüm kontrolünde değil ve bilinen bir kaynağı yok — büyük olasılıkla tek kopya bu.",
                "This folder is not under version control and has no known source — this is probably the only copy.")));
        }
        else
        {
            warnings.Add(new PlanWarning(WarningLevel.Warning, L.T(
                $"Git yok; yeniden indirilebilir ({item.WebUrl}) ama yerel değişiklikler kaybolur.",
                $"No git; it can be downloaded again ({item.WebUrl}) but local changes will be lost.")));
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

        foreach (var shortcut in FindShortcutsInto(path, ct))
        {
            steps.Add(new UninstallStep
            {
                Kind = UninstallStepKind.RemoveShortcut,
                Path = shortcut,
                Optional = true,
                Description = L.T($"Kısayolu kaldır: {Path.GetFileName(shortcut)}", $"Remove shortcut: {Path.GetFileName(shortcut)}"),
            });
        }

        foreach (var venv in FindRelatedVirtualenvs(item, path))
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
                Description = L.T(
                    $"İlgili sanal ortamı da kaldır ({FormatBytes(venvSize)}): {venv}",
                    $"Also remove the related virtual environment ({FormatBytes(venvSize)}): {venv}"),
            });
        }
    }

    private static void AddGitWarnings(GitInfo? info, List<PlanWarning> warnings)
    {
        if (info is null)
        {
            warnings.Add(new PlanWarning(WarningLevel.Danger, L.T(
                "Git durumu okunamadı; kaydedilmemiş iş olup olmadığı doğrulanamadı.",
                "Could not read the git state; unsaved work could not be ruled out.")));
            return;
        }

        if (string.IsNullOrWhiteSpace(info.RemoteUrl))
        {
            warnings.Add(new PlanWarning(WarningLevel.Danger, L.T(
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

            if (!info.HasUpstream && info.LastCommitAt is not null)
            {
                parts.Add(L.T(
                    $"'{info.Branch ?? "?"}' dalının uzakta karşılığı yok",
                    $"branch '{info.Branch ?? "?"}' has no upstream"));
            }
            else if (info.UpstreamGone && info.LastCommitAt is not null)
            {
                parts.Add(L.T(
                    $"'{info.Branch ?? "?"}' dalının uzaktaki karşılığı silinmiş",
                    $"the upstream of branch '{info.Branch ?? "?"}' was deleted"));
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

            warnings.Add(new PlanWarning(WarningLevel.Danger, L.T(
                $"Kaydedilmemiş iş kaybolacak: {string.Join(", ", parts)}.",
                $"Unsaved work will be lost: {string.Join(", ", parts)}.")));
        }
    }

    private void PlanInstalledApp(CatalogItem item, bool recycle, List<UninstallStep> steps, List<PlanWarning> warnings)
    {
        if (item.UninstallCommand is null)
        {
            warnings.Add(new PlanWarning(WarningLevel.Blocker, L.T(
                "Bu uygulamanın kaldırıcısı bulunamadı. Windows Ayarlar › Uygulamalar'ı kullanın.",
                "No uninstaller was found for this app. Use Windows Settings › Apps.")));
            return;
        }

        var location = item.Path is not null && Path.IsPathFullyQualified(item.Path)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path))
            : null;
        var locationUsable = location is not null && guard.Check(location, PathOperation.Delete, DeletePolicy.InstalledAppLeftovers).Allowed;
        if (locationUsable)
        {
            AddStopProcessesStep(item.Id, location!, steps, warnings);
        }

        steps.Add(new UninstallStep
        {
            Kind = UninstallStepKind.RunCommand,
            Command = item.UninstallCommand,
            Description = L.T($"Uygulamanın kendi kaldırıcısını çalıştır: {item.UninstallCommand.Display}", $"Run the app's own uninstaller: {item.UninstallCommand.Display}"),
        });
        warnings.Add(new PlanWarning(WarningLevel.Info, L.T(
            "Üreticinin kaldırıcısı kendi penceresini açabilir.",
            "The vendor uninstaller may show its own window.")));

        if (locationUsable)
        {
            steps.Add(new UninstallStep
            {
                Kind = UninstallStepKind.RemoveDirectory,
                Path = location,
                Optional = true,
                Description = L.T(
                    $"Kaldırıcıdan sonra kalan dosyaları sil (varsa): {location}",
                    $"Delete leftover files after the uninstaller (if any): {location}") + " — " + DeleteDescription(location!, recycle),
            });
        }
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
        var external = processFinder.FindUnder(path).Count;
        var total = tracked + external;
        steps.Add(new UninstallStep
        {
            Kind = UninstallStepKind.StopProcesses,
            Path = path,
            Description = L.T($"Bu klasörden çalışan işlemleri durdur ({total})", $"Stop processes running from this folder ({total})"),
        });
        if (total > 0)
        {
            warnings.Add(new PlanWarning(WarningLevel.Info, L.T(
                $"{total} çalışan işlem durdurulacak.",
                $"{total} running process(es) will be stopped.")));
        }
    }

    private HashSet<string> FindRelatedVirtualenvs(CatalogItem item, string path)
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

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in options.VirtualenvRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var dirName = Path.GetFileName(dir);

                // pipenv records the owning project in <venv>\.project — exact match when present.
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
                        found.Add(dir);
                    }

                    continue;
                }

                if (names.Any(n => dirName.Equals(n, StringComparison.OrdinalIgnoreCase) || dirName.StartsWith(n + "-", StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(dir);
                }
            }
        }

        return found;
    }

    private List<string> FindShortcutsInto(string path, CancellationToken ct)
    {
        var result = new List<string>();
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

        var candidates = options.ShortcutRootsRecursive.SelectMany(r => Enumerate(r, true))
            .Concat(options.ShortcutRootsTopLevel.SelectMany(r => Enumerate(r, false)));
        foreach (var lnk in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (++enumerated > 5000)
            {
                break;
            }

            if (ShortcutReader.TryGetTarget(lnk) is { } target && Path.IsPathFullyQualified(target) && PathGuard.IsSameOrUnder(target, path))
            {
                result.Add(lnk);
            }
        }

        return result;
    }

    // ───────────────────────────── execution ─────────────────────────────

    private async Task ExecuteStepAsync(UninstallPlan plan, PlanContext context, UninstallStep step, IProgress<string>? progress, CancellationToken ct)
    {
        var policy = context.Kind == ItemKind.InstalledApp ? DeletePolicy.InstalledAppLeftovers : DeletePolicy.Default;
        switch (step.Kind)
        {
            case UninstallStepKind.StopProcesses:
                await StopProcessesAsync(plan.ItemId, step.Path, ct).ConfigureAwait(false);
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

                break;

            case UninstallStepKind.RemoveDirectory:
                var target = step.Path ?? throw new InvalidOperationException("RemoveDirectory step without path.");
                if (!Path.Exists(target))
                {
                    // e.g. the vendor uninstaller already removed its install folder — nothing left to do.
                    progress?.Report(L.T($"Zaten yok: {target}", $"Already gone: {target}"));
                    break;
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
                    || ShortcutReader.TryGetTarget(lnk) is not { } lnkTarget || !Path.IsPathFullyQualified(lnkTarget)
                    || !PathGuard.IsSameOrUnder(lnkTarget, Path.GetFullPath(context.ItemPath)))
                {
                    throw new InvalidOperationException(L.T("Kısayol artık bu öğeyi göstermiyor; dokunulmadı.", "The shortcut no longer points to this item; left untouched."));
                }

                await remover.RemoveFileAsync(lnk, context.Recycle, DeletePolicy.Default, ct).ConfigureAwait(false);
                break;

            case UninstallStepKind.RemoveCatalogEntry:
                await catalog.RemoveItemAsync(plan.ItemId, ct).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"Unknown step kind {step.Kind}.");
        }
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

    private sealed record PlanContext(ItemKind Kind, string? ItemPath, bool Recycle);
}
