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
            : Task.Run(
                () =>
                {
                    Validate(full, policy);
                    DeleteTreePermanently(full, isRoot: true, ct);
                },
                ct);
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
        Win32.MoveToRecycleBin(full);
        if (Path.Exists(full))
        {
            throw new IOException(L.T($"Silinemedi (bazı dosyalar kullanımda olabilir): {full}", $"Could not remove (some files may be in use): {full}"));
        }
    }

    /// <summary>
    /// Recursive delete that removes links themselves instead of descending into them, and clears read-only attributes
    /// (git object files are read-only).
    /// </summary>
    internal static void DeleteTreePermanently(string directory, bool isRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new DirectoryInfo(directory);
        if (!isRoot && (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            RemoveLink(info);
            return;
        }

        foreach (var entry in info.EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        }))
        {
            ct.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
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

        Retry(() =>
        {
            info.Attributes = FileAttributes.Directory;
            info.Delete(recursive: false);
        });
    }

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
