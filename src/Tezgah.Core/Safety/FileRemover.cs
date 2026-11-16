using Microsoft.Win32.SafeHandles;
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

    private const uint DirectoryAccess = NativeMethods.FileListDirectory | NativeMethods.Delete
        | NativeMethods.FileReadAttributes | NativeMethods.FileWriteAttributes;

    private const uint EntryAccess = NativeMethods.Delete | NativeMethods.FileReadAttributes | NativeMethods.FileWriteAttributes;

    // Folders being emptied are held without FILE_SHARE_DELETE: nobody can rename them away (or swap a junction in
    // their place) while Tezgah works inside them.
    private const uint DirectoryShare = NativeMethods.FileShareRead | NativeMethods.FileShareWrite;

    /// <summary>
    /// Recursive delete that removes links themselves instead of descending into them, and deletes read-only files (git
    /// object files are read-only). Works on handles: every folder is opened without following links, checked through
    /// that handle, listed through it, and each entry is opened relative to it (again without following) and deleted
    /// through its own handle. A rename or junction swap racing the delete therefore can never redirect it to another
    /// tree; at worst the swapped-in link itself is removed.
    /// </summary>
    internal static void DeleteTreePermanently(string directory, bool isRoot, CancellationToken ct) =>
        DeleteTreePermanently(directory, isRoot, hooks: null, ct);

    internal static void DeleteTreePermanently(string directory, bool isRoot, DeleteTreeHooks? hooks, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var handle = Retry(() => FileHandles.Open(directory, DirectoryAccess, DirectoryShare, followReparse: false));
        var tag = FileHandles.GetAttributeTag(handle, directory);
        if (IsLinkLike(tag) || (tag.FileAttributes & (uint)FileAttributes.Directory) == 0)
        {
            if (isRoot)
            {
                // The guard refused links moments ago: a link here means the folder was swapped. Touch nothing.
                throw new IOException(L.T($"Klasör silinmeden hemen önce bir bağlantıyla değiştirildi; dokunulmadı: {directory}", $"The folder was replaced by a link right before deletion; left untouched: {directory}"));
            }

            Retry(() => FileHandles.DeleteByHandle(handle, directory));
            return;
        }

        if (IsReparsePoint(tag))
        {
            // Benign reparse folder (OneDrive placeholder): list it through the file system filter, but only if that
            // reaches the very folder that was just checked.
            using var listing = FileHandles.Open(directory, NativeMethods.FileListDirectory | NativeMethods.FileReadAttributes, NativeMethods.FileShareAll, followReparse: true);
            EnsureSameObject(handle, listing, directory);
            DeleteContents(listing, directory, isRoot, hooks, ct);
        }
        else
        {
            DeleteContents(handle, directory, isRoot, hooks, ct);
        }

        Retry(() => FileHandles.DeleteByHandle(handle, directory));
    }

    private static void DeleteContents(SafeFileHandle directory, string path, bool isRoot, DeleteTreeHooks? hooks, CancellationToken ct)
    {
        hooks?.BeforeList?.Invoke(path);
        (string Name, FileAttributes Attributes)? history = null;
        foreach (var entry in FileHandles.List(directory, path))
        {
            ct.ThrowIfCancellationRequested();
            if (isRoot && entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                history = entry; // deleted last: if anything else fails, the repository history survives
                continue;
            }

            DeleteEntry(directory, path, entry.Name, entry.Attributes, hooks, ct);
        }

        if (history is { } h)
        {
            DeleteEntry(directory, path, h.Name, h.Attributes, hooks, ct);
        }
    }

    private static void DeleteEntry(SafeFileHandle parent, string parentPath, string name, FileAttributes listed, DeleteTreeHooks? hooks, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.Join(parentPath, name);
        hooks?.BeforeOpen?.Invoke(path);
        var isDirectory = (listed & FileAttributes.Directory) != 0;
        using var entry = Retry(() => FileHandles.OpenChild(parent, name, isDirectory ? DirectoryAccess : EntryAccess,
            isDirectory ? DirectoryShare : NativeMethods.FileShareAll, followReparse: false, isDirectory, path));
        if (entry is null)
        {
            return; // already gone
        }

        var tag = FileHandles.GetAttributeTag(entry, path);
        if (isDirectory && !IsLinkLike(tag))
        {
            if (IsReparsePoint(tag))
            {
                using var listing = FileHandles.OpenChild(parent, name, NativeMethods.FileListDirectory | NativeMethods.FileReadAttributes,
                    NativeMethods.FileShareAll, followReparse: true, isDirectory: true, path)
                    ?? throw new IOException(L.T($"Klasör silme sırasında değişti: {path}", $"The folder changed during deletion: {path}"));
                EnsureSameObject(entry, listing, path);
                DeleteContents(listing, path, isRoot: false, hooks, ct);
            }
            else
            {
                DeleteContents(entry, path, isRoot: false, hooks, ct);
            }
        }

        // A link (junction, symlink) is deleted as itself: the handle was opened without following it.
        Retry(() => FileHandles.DeleteByHandle(entry, path));
    }

    private static void EnsureSameObject(SafeFileHandle expected, SafeFileHandle actual, string path)
    {
        var a = FileHandles.GetId(expected, path);
        var b = FileHandles.GetId(actual, path);
        if (a.VolumeSerialNumber != b.VolumeSerialNumber || a.FileIdLow != b.FileIdLow || a.FileIdHigh != b.FileIdHigh)
        {
            throw new IOException(L.T($"Klasör silme sırasında değişti; dokunulmadı: {path}", $"The folder changed during deletion; left untouched: {path}"));
        }
    }

    private static bool IsReparsePoint(FileAttributeTagInfo tag) => (tag.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0;

    internal static bool IsLinkLike(FileAttributeTagInfo tag) => IsReparsePoint(tag) && IsLinkLikeTag(tag.ReparseTag);

    /// <summary>
    /// True for reparse points that redirect the namespace (symlinks, junctions, mount points, unknown tags). Benign tags
    /// (OneDrive/cloud placeholders, dedup, WOF) are ordinary files and folders: a OneDrive project folder must be recursed
    /// into, not "unlinked" (which failed with "directory not empty" half-way). Fails closed when the tag cannot be read.
    /// </summary>
    internal static bool IsLinkLike(FileSystemInfo entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) != 0 && IsLinkLikeTag(Win32.GetReparseTag(entry.FullName));

    internal static bool IsLinkLikeTag(uint? tag) => tag is not { } t || !PathGuard.IsBenignReparseTag(t);

    private static T Retry<T>(Func<T> action)
    {
        var result = default(T)!;
        Retry(() => { result = action(); });
        return result;
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

/// <summary>Test seams for <see cref="FileRemover.DeleteTreePermanently(string, bool, DeleteTreeHooks?, CancellationToken)"/>.</summary>
internal sealed class DeleteTreeHooks
{
    /// <summary>Called with a folder's path after it was opened and checked, right before it is listed.</summary>
    public Action<string>? BeforeList { get; init; }

    /// <summary>Called with an entry's path after its folder was listed, right before the entry is opened.</summary>
    public Action<string>? BeforeOpen { get; init; }
}
