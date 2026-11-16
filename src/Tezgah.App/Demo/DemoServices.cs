using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tezgah.Core;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.App.Demo;

/// <summary>Builds believable uninstall plans and "executes" them by forgetting the item.</summary>
public sealed class DemoUninstallService(ICatalogService catalog, IRunService runs) : IUninstallService
{
    public async Task<UninstallPlan> PlanAsync(string itemId, CancellationToken ct)
    {
        await Task.Delay(250, ct).ConfigureAwait(false);
        var item = catalog.State.Items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new InvalidOperationException($"Unknown item {itemId}");
        var steps = new List<UninstallStep>();
        var warnings = new List<PlanWarning>();

        if (runs.ForItem(itemId).Count > 0)
        {
            steps.Add(new UninstallStep { Kind = UninstallStepKind.StopProcesses, Description = L.T("Çalışan süreçleri durdur", "Stop running processes"), Path = item.Path });
        }

        if (item.UninstallCommand is { } cmd)
        {
            steps.Add(new UninstallStep { Kind = UninstallStepKind.RunCommand, Description = L.T("Kendi kaldırıcısını çalıştır", "Run its own uninstaller"), Command = cmd });
        }

        if (item.Path is { } path && item.Kind is ItemKind.GitRepository or ItemKind.ProjectFolder)
        {
            steps.Add(new UninstallStep { Kind = UninstallStepKind.RemoveDirectory, Description = L.T("Klasörü Geri Dönüşüm Kutusu'na taşı", "Move folder to the Recycle Bin"), Path = path, SizeBytes = item.SizeBytes });
            if (item.ProjectTypes.Contains("python"))
            {
                var related = Path.Combine(DemoData.Home, ".cache", "huggingface", item.Name);
                steps.Add(new UninstallStep { Kind = UninstallStepKind.RemoveDirectory, Description = L.T("İlgili model önbelleği", "Related model cache"), Path = related, SizeBytes = 740L * 1024 * 1024, Optional = true });
            }
        }

        if (item.Kind == ItemKind.InstalledApp)
        {
            steps.Add(new UninstallStep { Kind = UninstallStepKind.RemoveShortcut, Description = L.T("Masaüstü kısayolu", "Desktop shortcut"), Path = Path.Combine(DemoData.Home, "Desktop", item.Name + ".lnk"), Optional = true });
            if (item.Path is { } appPath)
            {
                steps.Add(new UninstallStep { Kind = UninstallStepKind.RemoveDirectory, Description = L.T("Kaldırıcıdan sonra kalan klasör", "Leftover folder after the uninstaller"), Path = appPath, SizeBytes = item.SizeBytes / 20, Optional = true });
            }
        }

        steps.Add(new UninstallStep { Kind = UninstallStepKind.RemoveCatalogEntry, Description = L.T("Tezgah listesinden çıkar", "Remove from the Tezgah list") });

        if (item.Git is { HasUnsavedWork: true } git)
        {
            var parts = new List<string>();
            if (git.Ahead > 0)
            {
                parts.Add(L.T($"{git.Ahead} commit push edilmedi", $"{git.Ahead} commit(s) not pushed"));
            }

            if (git.ModifiedFiles + git.UntrackedFiles > 0)
            {
                parts.Add(L.T($"{git.ModifiedFiles + git.UntrackedFiles} kaydedilmemiş dosya", $"{git.ModifiedFiles + git.UntrackedFiles} uncommitted file(s)"));
            }

            if (git.Stashes > 0)
            {
                parts.Add(L.T($"{git.Stashes} stash", $"{git.Stashes} stash(es)"));
            }

            if (parts.Count == 0)
            {
                parts.Add(L.T("upstream yok, commit'ler yalnız bu bilgisayarda", "no upstream, commits exist only on this machine"));
            }

            warnings.Add(new PlanWarning(WarningLevel.Danger, L.T("Başka yerde olmayan iş kaybolacak: ", "Work that exists nowhere else will be lost: ") + string.Join(", ", parts)));
        }

        if (item.Name.Contains("WebView2", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new PlanWarning(WarningLevel.Blocker, L.T("Bu bir Windows bileşeni; birçok uygulama ona bağlı. Kaldırılamaz.", "This is a Windows component many apps depend on. It cannot be removed.")));
        }

        if (item.Kind is ItemKind.GitRepository or ItemKind.ProjectFolder)
        {
            warnings.Add(new PlanWarning(WarningLevel.Info, L.T("Dosyalar Geri Dönüşüm Kutusu'na taşınır; oradan geri alabilirsin.", "Files go to the Recycle Bin; you can restore them from there.")));
        }

        if (item.Kind == ItemKind.InstalledApp)
        {
            warnings.Add(new PlanWarning(WarningLevel.Warning, L.T("Uygulamanın kaldırıcısı kendi penceresini açabilir.", "The app's uninstaller may open its own window.")));
        }

        return new UninstallPlan { ItemId = itemId, ItemName = item.Name, Steps = steps, Warnings = warnings };
    }

    public async Task<UninstallResult> ExecuteAsync(UninstallPlan plan, IReadOnlyCollection<UninstallStep> selectedSteps, string? typedConfirmation, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selectedSteps);
        if (plan.IsBlocked)
        {
            throw new InvalidOperationException("Plan is blocked.");
        }

        if (plan.RequiresTypedConfirmation && !string.Equals(typedConfirmation, plan.ItemName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Typed confirmation does not match.");
        }

        var outcomes = new List<StepOutcome>();
        foreach (var step in plan.Steps)
        {
            if (step.Optional && !selectedSteps.Contains(step))
            {
                continue;
            }

            progress?.Report(step.Description + "…");
            await Task.Delay(step.Kind == UninstallStepKind.RunCommand ? 1200 : 500, ct).ConfigureAwait(false);
            if (step.Kind == UninstallStepKind.StopProcesses)
            {
                foreach (var p in runs.ForItem(plan.ItemId))
                {
                    await runs.StopAsync(p.RunId, ct).ConfigureAwait(false);
                }
            }

            if (step.Kind == UninstallStepKind.RemoveCatalogEntry)
            {
                await catalog.RemoveItemAsync(plan.ItemId, ct).ConfigureAwait(false);
            }

            outcomes.Add(new StepOutcome(step, true, null));
        }

        return new UninstallResult(outcomes);
    }
}

public sealed class DemoCleanupService(ICatalogService catalog) : ICleanupService
{
    public async Task<long> CleanArtifactsAsync(string itemId, IReadOnlyCollection<ArtifactDir> artifacts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        await Task.Delay(700, ct).ConfigureAwait(false);
        var freed = artifacts.Sum(a => a.SizeBytes);
        var item = catalog.State.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is not null && catalog is DemoCatalogService demo)
        {
            demo.ReplaceItem(item with
            {
                Artifacts = [.. item.Artifacts.Where(a => !artifacts.Contains(a))],
                SizeBytes = item.SizeBytes is { } s ? Math.Max(0, s - freed) : null,
            });
        }

        return freed;
    }
}

public sealed class DemoGitClient(ICatalogService catalog) : IGitClient
{
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<GitInfo?> GetInfoAsync(string repoPath, bool fetch, CancellationToken ct) =>
        Task.FromResult(catalog.State.Items.FirstOrDefault(i => string.Equals(i.Path, repoPath, StringComparison.OrdinalIgnoreCase))?.Git);

    public async Task<CommandResult> PullAsync(string repoPath, CancellationToken ct)
    {
        await Task.Delay(900, ct).ConfigureAwait(false);
        var item = catalog.State.Items.FirstOrDefault(i => string.Equals(i.Path, repoPath, StringComparison.OrdinalIgnoreCase));
        if (item?.Git is { } git && git.ModifiedFiles > 0)
        {
            return new CommandResult(1, string.Empty, "error: Your local changes to the following files would be overwritten by merge.\nPlease commit your changes or stash them before you merge.", false);
        }

        if (item?.Git is { Behind: > 0 } g && catalog is DemoCatalogService demo)
        {
            demo.ReplaceItem(item with { Git = g with { Behind = 0 }, LastActivity = DateTimeOffset.Now });
            return new CommandResult(0, $"Updating 3f2a1c9..9b7e4d2\nFast-forward\n {g.Behind} commits pulled.", string.Empty, false);
        }

        return new CommandResult(0, "Already up to date.", string.Empty, false);
    }
}

public static class DemoRegistration
{
    /// <summary>
    /// Registers the demo fakes. <paramref name="replace"/> = true (the <c>--demo</c> switch) swaps out real services so
    /// the sample data is consistent; otherwise only missing services are filled (<c>TryAdd</c>).
    /// </summary>
    public static IServiceCollection AddTezgahDemo(this IServiceCollection services, bool replace, bool startEmpty)
    {
        if (replace)
        {
            services.RemoveAll<ICatalogService>();
            services.RemoveAll<IRunService>();
            services.RemoveAll<IUninstallService>();
            services.RemoveAll<ICleanupService>();
            services.RemoveAll<IGitClient>();
        }

        services.TryAddSingleton(new DemoOptions(startEmpty));
        services.TryAddSingleton<DemoCatalogService>();
        services.TryAddSingleton<ICatalogService>(sp => sp.GetRequiredService<DemoCatalogService>());
        services.TryAddSingleton<IRunService, DemoRunService>();
        services.TryAddSingleton<IUninstallService, DemoUninstallService>();
        services.TryAddSingleton<ICleanupService, DemoCleanupService>();
        services.TryAddSingleton<IGitClient, DemoGitClient>();
        return services;
    }
}
