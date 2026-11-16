using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tezgah.Core.Interop;

/// <summary>UNICODE_STRING.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct UnicodeString
{
    public ushort Length;
    public ushort MaximumLength;
    public char* Buffer;
}

/// <summary>OBJECT_ATTRIBUTES.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ObjectAttributes
{
    public uint Length;
    public nint RootDirectory;
    public UnicodeString* ObjectName;
    public uint Attributes;
    public nint SecurityDescriptor;
    public nint SecurityQualityOfService;
}

/// <summary>IO_STATUS_BLOCK.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoStatusBlock
{
    public nint Status;
    public nuint Information;
}

/// <summary>FILE_ATTRIBUTE_TAG_INFO.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileAttributeTagInfo
{
    public uint FileAttributes;
    public uint ReparseTag;
}

/// <summary>FILE_BASIC_INFO. Zero times (and zero attributes) mean "leave unchanged".</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileBasicInfo
{
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public uint FileAttributes;
}

/// <summary>FILE_ID_INFO: volume serial number plus the 128-bit file id.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileIdInfo
{
    public ulong VolumeSerialNumber;
    public ulong FileIdLow;
    public ulong FileIdHigh;
}

/// <summary>Handle-relative file operations (open below a directory handle, enumerate, delete by handle).</summary>
internal static unsafe partial class NativeMethods
{
    public const uint Delete = 0x00010000;
    public const uint Synchronize = 0x00100000;
    public const uint FileListDirectory = 0x0001;
    public const uint FileWriteAttributes = 0x0100;
    public const uint FileShareRead = 0x1;
    public const uint FileShareWrite = 0x2;
    public const uint FileFlagOpenReparsePoint = 0x00200000;

    public const uint ObjCaseInsensitive = 0x40;
    public const uint FileOpen = 1;
    public const uint FileDirectoryFile = 0x00000001;
    public const uint FileSynchronousIoNonAlert = 0x00000020;
    public const uint FileNonDirectoryFile = 0x00000040;
    public const uint FileOpenForBackupIntent = 0x00004000;
    public const uint FileOpenReparsePoint = 0x00200000;

    public const int FileBasicInfoClass = 0;
    public const int FileDispositionInfoClass = 4;
    public const int FileAttributeTagInfoClass = 9;
    public const int FileFullDirectoryInfoClass = 14;
    public const int FileFullDirectoryRestartInfoClass = 15;
    public const int FileIdInfoClass = 18;
    public const int FileDispositionInfoExClass = 21;

    public const uint FileDispositionFlagDelete = 0x1;
    public const uint FileDispositionFlagPosixSemantics = 0x2;
    public const uint FileDispositionFlagIgnoreReadonlyAttribute = 0x10;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, void* info, uint size);

    [LibraryImport("ntdll.dll")]
    public static partial int NtCreateFile(out SafeFileHandle handle, uint desiredAccess, ObjectAttributes* objectAttributes,
        IoStatusBlock* ioStatus, long* allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition,
        uint createOptions, void* eaBuffer, uint eaLength);

    [LibraryImport("ntdll.dll")]
    public static partial uint RtlNtStatusToDosError(int status);
}
