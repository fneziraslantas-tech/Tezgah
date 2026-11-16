using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;
using Tezgah.Core.Safety;

namespace Tezgah.Core.Uninstall;

/// <summary>
/// Deletes regenerable artifact folders (node_modules, .venv, bin/obj, target...). Every folder must be one the catalog
/// detected for the item, lie strictly inside the item folder after full normalisation, pass the path guard (which refuses
/// links anywhere in the chain) and must not be or be inside a <c>.git</c> folder. All are validated before any is deleted.
/// </summary>
internal sealed class CleanupService(ICatalogService catalog, PathGuard guard, FileRemover remover, UninstallOptions options, IGitClient? git = null) : ICleanupService
{
    private readonly AuditLog _audit = new(options.AuditLogPath);

    public async Task<long> CleanArtifactsAsync(string itemId, IReadOnlyCollection<ArtifactDir> artifacts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        ArgumentNullException.ThrowIfNull(artifacts);
        var item = catalog.State.Items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new KeyNotFoundException(L.T($"Öğe bulunamadı: {itemId}", $"Item not found: {itemId}"));
        if (item.Path is null || !Path.IsPathFullyQualified(item.Path))
        {
            throw new InvalidOperationException(L.T("Öğenin klasörü bilinmiyor.", "The item's folder is unknown."));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path));
        var known = new HashSet<string>(
            item.Artifacts.Where(a => Path.IsPathFullyQualified(a.Path)).Select(a => Path.TrimEndingDirectorySeparator(Path.GetFullPath(a.Path))),
            StringComparer.OrdinalIgnoreCase);

        var targets = new List<string>();
        foreach (var artifact in artifacts)
        {
            targets.Add(Validate(artifact, root, known));
        }

        // build/dist/out are often hand-made or committed (electron-builder icons, a GitHub action's dist...). In a git
        // repository they are only cleaned when git says everything in them is ignored. Checked before anything is deleted.
        if (Discovery.ProjectInspector.IsGitRoot(root))
        {
            foreach (var target in targets.Where(t => ArtifactDir.AmbiguousNames.Contains(Path.GetFileName(t))))
            {
                var holds = git is null ? null : await git.HoldsUnregenerableFilesAsync(root, Path.GetRelativePath(root, target), ct).ConfigureAwait(false);
                if (holds != false)
                {
                    throw new UnauthorizedAccessException(L.T(
                        $"Bu klasörde git'in izlediği ya da yok saymadığı dosyalar var (ya da doğrulanamadı); temizlenmedi: {target}",
                        $"This folder holds files git tracks or does not ignore (or this could not be verified); not cleaned: {target}"));
                }
            }
        }

        var recycle = catalog.State.Settings.UseRecycleBin;
        long freed = 0;
        var auditSteps = new List<AuditStep>();
        try
        {
            foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(target))
                {
                    continue;
                }

                var size = DirectorySize.Measure(target, ct);
                try
                {
                    await remover.RemoveDirectoryAsync(target, recycle, DeletePolicy.Default, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    auditSteps.Add(new AuditStep(UninstallStepKind.RemoveDirectory, target, null, false, ex.Message));
                    throw;
                }

                auditSteps.Add(new AuditStep(UninstallStepKind.RemoveDirectory, target, null, true, null));
                freed += size;
            }
        }
        finally
        {
            if (auditSteps.Count > 0)
            {
                await _audit.TryWriteAsync(
                    new AuditEntry(DateTimeOffset.Now, "cleanup", item.Id, item.Name, auditSteps.All(s => s.Success) ? "success" : "failed", auditSteps),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        try
        {
            await catalog.RefreshItemAsync(itemId, fetchGit: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or KeyNotFoundException)
        {
            // refresh is cosmetic; the space is freed either way
        }

        return freed;
    }

    private string Validate(ArtifactDir artifact, string root, HashSet<string> known)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        string Refuse(string tr, string en) => throw new UnauthorizedAccessException(L.T(tr, en) + $" ({artifact.Path})");

        if (string.IsNullOrWhiteSpace(artifact.Path) || !Path.IsPathFullyQualified(artifact.Path))
        {
            return Refuse("Geçersiz klasör yolu.", "Invalid folder path.");
        }

        var check = guard.Check(artifact.Path, PathOperation.Delete);
        if (!check.Allowed)
        {
            throw new UnauthorizedAccessException(check.Reason);
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifact.Path));
        if (!PathGuard.IsStrictlyUnder(full, root))
        {
            return Refuse("Klasör öğenin içinde değil.", "The folder is not inside the item.");
        }

        var relative = Path.GetRelativePath(root, full);
        if (relative.Split('\\', '/').Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            return Refuse(".git klasörü temizlenemez.", "The .git folder cannot be cleaned.");
        }

        if (!known.Contains(full))
        {
            return Refuse("Bu klasör öğe için algılanan bir yapı çıktısı değil.", "This folder is not a detected build artifact of the item.");
        }

        // The guard refused links in the chain, so the logical path is the real one; double-check the item root too.
        if (!guard.Check(root, PathOperation.Read).Allowed)
        {
            return Refuse("Öğe klasörü doğrulanamadı.", "The item folder could not be verified.");
        }

        return full;
    }
}
