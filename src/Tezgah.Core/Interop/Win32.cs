using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tezgah.Core.Interop;

/// <summary>Managed wrappers around the raw P/Invoke declarations.</summary>
internal static unsafe class Win32
{
    /// <summary>
    /// Opens the path (following every link in the chain) and returns the path the OS actually resolved,
    /// without the <c>\\?\</c> prefix. UNC targets come back as <c>\\server\share\...</c>. Null if it cannot be opened.
    /// </summary>
    public static string? GetFinalPath(string path)
    {
        using var handle = NativeMethods.CreateFile(path, NativeMethods.FileReadAttributes, NativeMethods.FileShareAll, 0,
            NativeMethods.OpenExisting, NativeMethods.FileFlagBackupSemantics, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        uint size = 512;
        while (true)
        {
            var buffer = new char[size];
            uint written;
            fixed (char* p = buffer)
            {
                written = NativeMethods.GetFinalPathNameByHandle(handle, p, size, NativeMethods.VolumeNameDos);
            }

            if (written == 0)
            {
                return null;
            }

            if (written < size)
            {
                var result = new string(buffer, 0, (int)written);
                if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    return @"\\" + result[8..];
                }

                return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
            }

            size = written + 1;
        }
    }

    /// <summary>Expands 8.3 short segments (C:\PROGRA~1 → C:\Program Files). Returns null when the path does not exist.</summary>
    public static string? GetLongPath(string path)
    {
        uint size = 512;
        while (true)
        {
            var buffer = new char[size];
            uint written;
            fixed (char* p = buffer)
            {
                written = NativeMethods.GetLongPathName(path, p, size);
            }

            if (written == 0)
            {
                return null;
            }

            if (written < size)
            {
                return new string(buffer, 0, (int)written);
            }

            size = written + 1;
        }
    }

    /// <summary>Reparse tag of the entry itself (does not follow it). Null when the entry is not a reparse point or is missing.</summary>
    public static uint? GetReparseTag(string path)
    {
        using var handle = NativeMethods.FindFirstFile(path, out var data);
        if (handle.IsInvalid)
        {
            return null;
        }

        return (data.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ? data.Reserved0 : null;
    }

    /// <summary>Full image path of a process, or null when access is denied / it already exited.</summary>
    public static string? TryGetProcessImagePath(int processId)
    {
        using var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle.IsInvalid)
        {
            return null;
        }

        uint size = 1024;
        var buffer = new char[size];
        fixed (char* p = buffer)
        {
            return NativeMethods.QueryFullProcessImageName(handle, 0, p, ref size) ? new string(p, 0, (int)size) : null;
        }
    }

    /// <summary>Moves a file or directory to the Recycle Bin. Must be called with a fully-qualified, already validated path.</summary>
    public static void MoveToRecycleBin(string path)
    {
        // pFrom must be double-null-terminated: "path\0\0".
        var from = (path + "\0\0").ToCharArray();
        const ushort flags = NativeMethods.FofAllowUndo | NativeMethods.FofNoConfirmation | NativeMethods.FofNoErrorUi
            | NativeMethods.FofSilent | NativeMethods.FofWantNukeWarning;
        int rc;
        bool aborted;
        fixed (char* pFrom = from)
        {
            if (Environment.Is64BitProcess)
            {
                var op = new ShFileOpStruct64 { Func = NativeMethods.FoDelete, From = pFrom, Flags = flags };
                rc = NativeMethods.SHFileOperation64(&op);
                aborted = op.AnyOperationsAborted != 0;
            }
            else
            {
                var op = new ShFileOpStruct32 { Func = NativeMethods.FoDelete, From = pFrom, Flags = flags };
                rc = NativeMethods.SHFileOperation32(&op);
                aborted = op.AnyOperationsAborted != 0;
            }
        }

        if (rc != 0)
        {
            throw new IOException(L.T(
                $"Geri Dönüşüm Kutusu'na taşınamadı (kod 0x{rc:X}): {path}",
                $"Could not move to the Recycle Bin (code 0x{rc:X}): {path}"));
        }

        if (aborted)
        {
            throw new OperationCanceledException(L.T("Silme işlemi iptal edildi.", "The delete operation was aborted."));
        }
    }

    /// <summary>Throws a <see cref="Win32Exception"/> for the last P/Invoke error.</summary>
    [StackTraceHidden]
    public static Win32Exception LastError(string what) => new(Marshal.GetLastPInvokeError(), what);
}
