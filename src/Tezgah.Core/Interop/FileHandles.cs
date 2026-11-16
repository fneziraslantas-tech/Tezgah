using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tezgah.Core.Interop;

/// <summary>
/// Handle-based file system primitives for code that must not be redirected by a concurrent rename/junction swap:
/// entries are opened relative to an already-open directory handle with FILE_OPEN_REPARSE_POINT (a link is opened as
/// itself, never followed), inspected and deleted through that handle. No path string is re-resolved.
/// </summary>
internal static unsafe class FileHandles
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;

    /// <summary>Opens <paramref name="path"/> as a directory-capable handle; with <paramref name="followReparse"/> false a link is opened as itself.</summary>
    public static SafeFileHandle Open(string path, uint access, uint share, bool followReparse)
    {
        var handle = NativeMethods.CreateFile(path, access | NativeMethods.Synchronize, share, 0, NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | (followReparse ? 0 : NativeMethods.FileFlagOpenReparsePoint), 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw ToException(error, path);
        }

        return handle;
    }

    /// <summary>
    /// Opens the entry <paramref name="name"/> (a single path component) inside the directory <paramref name="directory"/>.
    /// With <paramref name="followReparse"/> false the entry itself is opened even when it is a link. Returns null when the
    /// entry no longer exists. <paramref name="isDirectory"/> makes the open fail unless the entry is (or is not) a
    /// directory, so an entry swapped for something of the other kind is refused instead of being handled as the old one.
    /// </summary>
    public static SafeFileHandle? OpenChild(SafeFileHandle directory, string name, uint access, uint share, bool followReparse, bool isDirectory, string displayPath)
    {
        if (name.Length == 0 || name is "." or ".." || name.AsSpan().IndexOfAny('\\', '/') >= 0 || name.Length > short.MaxValue / 2)
        {
            throw new IOException(L.T($"Geçersiz girdi adı: {displayPath}", $"Invalid entry name: {displayPath}"));
        }

        var options = NativeMethods.FileSynchronousIoNonAlert | NativeMethods.FileOpenForBackupIntent
            | (followReparse ? 0 : NativeMethods.FileOpenReparsePoint)
            | (isDirectory ? NativeMethods.FileDirectoryFile : NativeMethods.FileNonDirectoryFile);
        fixed (char* p = name)
        {
            var objectName = new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = p };
            var addRef = false;
            directory.DangerousAddRef(ref addRef);
            try
            {
                var attributes = new ObjectAttributes
                {
                    Length = (uint)sizeof(ObjectAttributes),
                    RootDirectory = directory.DangerousGetHandle(),
                    ObjectName = &objectName,
                    Attributes = NativeMethods.ObjCaseInsensitive,
                };
                IoStatusBlock io;
                var status = NativeMethods.NtCreateFile(out var handle, access | NativeMethods.Synchronize, &attributes, &io, null, 0,
                    share, NativeMethods.FileOpen, options, null, 0);
                if (status >= 0)
                {
                    return handle;
                }

                handle.Dispose();
                var error = (int)NativeMethods.RtlNtStatusToDosError(status);
                return error is ErrorFileNotFound or ErrorPathNotFound ? null : throw ToException(error, displayPath);
            }
            finally
            {
                if (addRef)
                {
                    directory.DangerousRelease();
                }
            }
        }
    }

    /// <summary>Attributes and reparse tag of the open object itself.</summary>
    public static FileAttributeTagInfo GetAttributeTag(SafeFileHandle handle, string displayPath)
    {
        FileAttributeTagInfo info;
        if (!NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileAttributeTagInfoClass, &info, (uint)sizeof(FileAttributeTagInfo)))
        {
            throw ToException(Marshal.GetLastPInvokeError(), displayPath);
        }

        return info;
    }

    /// <summary>Volume serial number and file id of the open object.</summary>
    public static FileIdInfo GetId(SafeFileHandle handle, string displayPath)
    {
        FileIdInfo info;
        if (!NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileIdInfoClass, &info, (uint)sizeof(FileIdInfo)))
        {
            throw ToException(Marshal.GetLastPInvokeError(), displayPath);
        }

        return info;
    }

    /// <summary>Names and attributes of the entries of an open directory (without "." and "..").</summary>
    public static List<(string Name, FileAttributes Attributes)> List(SafeFileHandle directory, string displayPath)
    {
        // FILE_FULL_DIR_INFO: NextEntryOffset(4) FileIndex(4) 4 times(32) EndOfFile(8) AllocationSize(8)
        // FileAttributes(4) FileNameLength(4) EaSize(4) FileName[].
        const int attributesOffset = 56;
        const int nameLengthOffset = 60;
        const int nameOffset = 68;
        var names = new List<(string, FileAttributes)>();
        var buffer = new byte[64 * 1024];
        var infoClass = NativeMethods.FileFullDirectoryRestartInfoClass;
        fixed (byte* b = buffer)
        {
            while (true)
            {
                if (!NativeMethods.GetFileInformationByHandleEx(directory, infoClass, b, (uint)buffer.Length))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == ErrorNoMoreFiles)
                    {
                        return names;
                    }

                    throw ToException(error, displayPath);
                }

                infoClass = NativeMethods.FileFullDirectoryInfoClass;
                var offset = 0;
                while (true)
                {
                    var entry = b + offset;
                    var next = *(uint*)entry;
                    var nameBytes = *(uint*)(entry + nameLengthOffset);
                    if (offset + nameOffset + nameBytes > buffer.Length)
                    {
                        throw new IOException(L.T($"Klasör listesi okunamadı: {displayPath}", $"Could not read the folder listing: {displayPath}"));
                    }

                    var name = new string((char*)(entry + nameOffset), 0, (int)(nameBytes / 2));
                    if (name is not ("." or ".."))
                    {
                        names.Add((name, (FileAttributes)(*(uint*)(entry + attributesOffset))));
                    }

                    if (next == 0)
                    {
                        break;
                    }

                    offset += (int)next;
                }
            }
        }
    }

    /// <summary>
    /// Deletes the open object (the link itself when it was opened without following). The handle needs DELETE and
    /// FILE_WRITE_ATTRIBUTES access. Read-only files are deleted too (git object files are read-only).
    /// </summary>
    public static void DeleteByHandle(SafeFileHandle handle, string displayPath)
    {
        var flags = NativeMethods.FileDispositionFlagDelete | NativeMethods.FileDispositionFlagPosixSemantics
            | NativeMethods.FileDispositionFlagIgnoreReadonlyAttribute;
        if (NativeMethods.SetFileInformationByHandle(handle, NativeMethods.FileDispositionInfoExClass, &flags, sizeof(uint)))
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error is not (ErrorInvalidParameter or ErrorNotSupported or ErrorInvalidFunction))
        {
            throw ToException(error, displayPath);
        }

        // Older Windows or a file system without POSIX delete: clear read-only by handle, then classic delete-on-close.
        var attributes = GetAttributeTag(handle, displayPath).FileAttributes;
        if ((attributes & (uint)FileAttributes.ReadOnly) != 0)
        {
            var cleared = attributes & ~(uint)FileAttributes.ReadOnly;
            var basic = new FileBasicInfo { FileAttributes = cleared == 0 ? (uint)FileAttributes.Normal : cleared };
            if (!NativeMethods.SetFileInformationByHandle(handle, NativeMethods.FileBasicInfoClass, &basic, (uint)sizeof(FileBasicInfo)))
            {
                throw ToException(Marshal.GetLastPInvokeError(), displayPath);
            }
        }

        var delete = (byte)1; // FILE_DISPOSITION_INFO { BOOLEAN DeleteFile; }
        if (!NativeMethods.SetFileInformationByHandle(handle, NativeMethods.FileDispositionInfoClass, &delete, sizeof(byte)))
        {
            throw ToException(Marshal.GetLastPInvokeError(), displayPath);
        }
    }

    private static Exception ToException(int error, string path)
    {
        var message = $"{Marshal.GetPInvokeErrorMessage(error)}: {path}";
        return error switch
        {
            ErrorAccessDenied => new UnauthorizedAccessException(message),
            ErrorFileNotFound => new FileNotFoundException(message, path),
            ErrorPathNotFound => new DirectoryNotFoundException(message),
            _ => new IOException(message, unchecked((int)0x80070000) | error),
        };
    }
}
