using Tezgah.Core.Abstractions;
using Tezgah.Core.Interop;
using Tezgah.Core.Model;

namespace Tezgah.Core.Safety;

/// <summary>
/// Deletes files and folders. Every call re-runs the <see cref="PathGuard"/> immediately before acting (TOCTOU: a folder
/// that was safe at plan time may have been swapped for a junction since). Permanent deletion never follows links.
/// </summary>
internal sealed class FileRemover(PathGuard guard) : IFileRemover
{
    public Task RemoveDirectoryAsync(string path, bool toRecycleBin, CancellationToken ct) =>
        RemoveDirectoryAsync(path, toRecycleBin, DeletePolicy.Default, ct);

    public Task RemoveFileAsync(string path, bool toRecycleBin, CancellationToken ct) =>
        RemoveFileAsync(path, toRecycleBin, DeletePolicy.Default, ct);

    public Task RemoveDirectoryAsync(string path, bool toRecycleBin, DeletePolicy policy, CancellationToken ct)
    {
        var full = Validate(path, policy);
        if (!Directory.Exists(full) || File.Exists(full))
        {
            throw new DirectoryNotFoundException(L.T($"Klasör bulunamadı: {full}", $"Folder not found: {full}"));
        }

        return toRecycleBin
            ? RunOnStaThreadAsync(() => RecycleAndVerify(full, policy), ct)
            : Task.Run(() => DeletePermanentlyWithPreflight(full, policy, ct), ct);
    }

    /// <summary>
    /// Permanent delete in two phases. First the folder is renamed to a hidden sibling ("tombstone") on the same volume:
    /// NTFS refuses that rename while anything inside is open or used as a current directory, so a folder that is in use
    /// fails here with nothing deleted (instead of half a tree, history first). Then the tombstone is deleted, history last.
    /// </summary>
    private void DeletePermanentlyWithPreflight(string full, DeletePolicy policy, CancellationToken ct)
    {
        Validate(full, policy);
        var parent = Path.GetDirectoryName(full)!;
        var tombstone = Path.Combine(parent, $".{Path.GetFileName(full)}.tezgah-delete-{Guid.NewGuid().ToString("N")[..8]}");
        try
        {
            Retry(() => Directory.Move(full, tombstone));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(L.T(
                $"Klasördeki dosyalar kullanımda; hiçbir şey silinmedi. Klasörü kullanan programları kapatıp tekrar deneyin: {full}",
                $"Files in the folder are in use; nothing was deleted. Close the programs using it and try again: {full}"), ex);
        }

        try
        {
            Validate(tombstone, policy);
            DeleteTreePermanently(tombstone, isRoot: true, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            try
            {
                if (Directory.Exists(tombstone) && !Path.Exists(full))
                {
                    Directory.Move(tombstone, full); // put what is left back where the user expects it
                }
            }
            catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
            {
                throw new IOException(L.T(
                    $"Silme yarıda kaldı; kalan dosyalar şurada: {tombstone}",
                    $"Deletion stopped part-way; the remaining files are in: {tombstone}"), ex);
            }

            throw;
        }
    }

    public Task RemoveFileAsync(string path, bool toRecycleBin, DeletePolicy policy, CancellationToken ct)
    {
        var full = Validate(path, policy);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(L.T($"Dosya bulunamadı: {full}", $"File not found: {full}"), full);
        }

        return toRecycleBin
            ? RunOnStaThreadAsync(() => RecycleAndVerify(full, policy), ct)
            : Task.Run(
                () =>
                {
                    Validate(full, policy);
                    File.SetAttributes(full, FileAttributes.Normal);
                    File.Delete(full);
                },
                ct);
    }

    private string Validate(string path, DeletePolicy policy)
    {
        var check = guard.Check(path, PathOperation.Delete, policy);
        if (!check.Allowed)
        {
            throw new UnauthorizedAccessException(check.Reason);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private void RecycleAndVerify(string full, DeletePolicy policy)
    {
        // Final re-check right before the shell call, on the thread that performs it.
        Validate(full, policy);
        try
        {
            Win32.MoveToRecycleBin(full);
        }
        catch (IOException) when (Directory.Exists(full) && ClearReadOnly(full) > 0)
        {
            // Some Windows editions (e.g. Server SKUs) refuse to recycle read-only files with DE_ACCESSDENIEDSRC;
            // git object files are read-only. The folder is about to go anyway, so drop the flag and try once more.
            Validate(full, policy);
            Win32.MoveToRecycleBin(full);
        }

        if (Path.Exists(full))
        {
            throw new IOException(L.T($"Silinemedi (bazı dosyalar kullanımda olabilir): {full}", $"Could not remove (some files may be in use): {full}"));
        }
    }

    /// <summary>
    /// Clears the read-only attribute on every file and folder under <paramref name="root"/> without descending into
    /// links. Returns how many entries were changed.
    /// </summary>
    internal static int ClearReadOnly(string root)
    {
        var changed = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (IsLinkLike(entry))
                {
                    continue;
                }

                if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    try
                    {
                        entry.Attributes &= ~FileAttributes.ReadOnly;
                        changed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // best effort
                    }
                }

                if (entry is DirectoryInfo sub)
                {
                    pending.Push(sub);
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Recursive delete that removes links themselves instead of descending into them, and clears read-only attributes
    /// (git object files are read-only).
    /// </summary>
    internal static void DeleteTreePermanently(string directory, bool isRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new DirectoryInfo(directory);
        info.Refresh(); // fresh attributes, never the ones cached by the parent's enumeration
        if (IsLinkLike(info))
        {
            if (isRoot)
            {
                // The guard refused links moments ago: a link here means the folder was swapped. Touch nothing.
                throw new IOException(L.T($"Klasör silinmeden hemen önce bir bağlantıyla değiştirildi; dokunulmadı: {directory}", $"The folder was replaced by a link right before deletion; left untouched: {directory}"));
            }

            RemoveLink(info);
            return;
        }

        FileSystemInfo? history = null;
        foreach (var entry in info.EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        }))
        {
            ct.ThrowIfCancellationRequested();
            if (isRoot && entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                history = entry; // deleted last: if anything else fails, the repository history survives
                continue;
            }

            DeleteEntry(entry, ct);
        }

        if (history is not null)
        {
            DeleteEntry(history, ct);
        }


        Retry(() =>
        {
            info.Attributes = FileAttributes.Directory;
            info.Delete(recursive: false);
        });
    }

    private static void DeleteEntry(FileSystemInfo entry, CancellationToken ct)
    {
        if (IsLinkLike(entry))
        {
            RemoveLink(entry);
        }
        else if (entry is DirectoryInfo dir)
        {
            DeleteTreePermanently(dir.FullName, isRoot: false, ct);
        }
        else
        {
            Retry(() =>
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            });
        }
    }

    /// <summary>
    /// True for reparse points that redirect the namespace (symlinks, junctions, mount points, unknown tags). Benign tags
    /// (OneDrive/cloud placeholders, dedup, WOF) are ordinary files and folders: a OneDrive project folder must be recursed
    /// into, not "unlinked" (which failed with "directory not empty" half-way). Fails closed when the tag cannot be read.
    /// </summary>
    internal static bool IsLinkLike(FileSystemInfo entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) != 0 && IsLinkLikeTag(Win32.GetReparseTag(entry.FullName));

    internal static bool IsLinkLikeTag(uint? tag) => tag is not { } t || !PathGuard.IsBenignReparseTag(t);

    /// <summary>Deletes a symlink/junction entry. RemoveDirectoryW/DeleteFileW act on the link, never on its target.</summary>
    private static void RemoveLink(FileSystemInfo link)
    {
        Retry(() =>
        {
            if ((link.Attributes & FileAttributes.ReadOnly) != 0)
            {
                link.Attributes &= ~FileAttributes.ReadOnly;
            }

            if (link is DirectoryInfo d)
            {
                d.Delete(recursive: false);
            }
            else
            {
                link.Delete();
            }
        });
    }

    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    /// <summary>Shell file operations are COM-based; run them on a dedicated STA background thread.</summary>
    private static Task RunOnStaThreadAsync(Action action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Tezgah recycle",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
