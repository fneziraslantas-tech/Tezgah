using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Every P/Invoke in this assembly targets a Windows system DLL: resolve them from System32 only, never from the
// application folder, the current directory or PATH.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Tezgah.Core.Interop;

/// <summary>Owns a Win32 Job Object handle. Closing the last handle of a KILL_ON_JOB_CLOSE job kills every process in it.</summary>
internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

/// <summary>FindFirstFileW search handle.</summary>
internal sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFindHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.FindClose(handle);
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectBasicLimitInformation
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IoCounters
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectExtendedLimitInformation
{
    public JobObjectBasicLimitInformation BasicLimitInformation;
    public IoCounters IoInfo;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Win32FindData
{
    public uint FileAttributes;
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public uint FileSizeHigh;
    public uint FileSizeLow;

    /// <summary>Reparse tag when <see cref="FileAttributes"/> has FILE_ATTRIBUTE_REPARSE_POINT.</summary>
    public uint Reserved0;
    public uint Reserved1;
    public fixed char FileName[260];
    public fixed char AlternateFileName[14];
}

/// <summary>SHFILEOPSTRUCTW for 64-bit processes (natural packing, see shellapi.h pshpack8).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ShFileOpStruct64
{
    public nint Hwnd;
    public uint Func;
    public char* From;
    public char* To;
    public ushort Flags;
    public int AnyOperationsAborted;
    public nint NameMappings;
    public char* ProgressTitle;
}

/// <summary>SHFILEOPSTRUCTW for 32-bit processes (pshpack1).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct ShFileOpStruct32
{
    public nint Hwnd;
    public uint Func;
    public char* From;
    public char* To;
    public ushort Flags;
    public int AnyOperationsAborted;
    public nint NameMappings;
    public char* ProgressTitle;
}

/// <summary>JOBOBJECT_BASIC_ACCOUNTING_INFORMATION.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectBasicAccountingInformation
{
    public long TotalUserTime;
    public long TotalKernelTime;
    public long ThisPeriodTotalUserTime;
    public long ThisPeriodTotalKernelTime;
    public uint TotalPageFaultCount;
    public uint TotalProcesses;
    public uint ActiveProcesses;
    public uint TotalTerminatedProcesses;
}

/// <summary>BY_HANDLE_FILE_INFORMATION. FILETIMEs are split into two DWORDs to keep the native 4-byte packing.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ByHandleFileInformation
{
    public uint FileAttributes;
    public uint CreationTimeLow;
    public uint CreationTimeHigh;
    public uint LastAccessTimeLow;
    public uint LastAccessTimeHigh;
    public uint LastWriteTimeLow;
    public uint LastWriteTimeHigh;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}

internal static unsafe partial class NativeMethods
{
    public const int JobObjectBasicAccountingInformationClass = 1;
    public const int JobObjectBasicProcessIdListClass = 3;
    public const int JobObjectExtendedLimitInformationClass = 9;
    public const uint JobObjectLimitKillOnJobClose = 0x00002000;

    public const uint ProcessQueryLimitedInformation = 0x1000;

    public const uint FileReadAttributes = 0x80;
    public const uint FileShareAll = 0x1 | 0x2 | 0x4;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint ReparseTagNameSurrogateBit = 0x20000000;
    public const uint VolumeNameDos = 0x0;

    public const uint FoDelete = 0x0003;
    public const ushort FofSilent = 0x0004;
    public const ushort FofNoConfirmation = 0x0010;
    public const ushort FofAllowUndo = 0x0040;
    public const ushort FofNoErrorUi = 0x0400;
    public const ushort FofWantNukeWarning = 0x4000;

    /// <summary>Application folder, System32 and AddDllDirectory folders; never the current directory or PATH.</summary>
    public const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetDefaultDllDirectories(uint directoryFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeJobHandle CreateJobObject(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(SafeJobHandle job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryInformationJobObject(SafeJobHandle job, int infoClass, void* info, uint length, nint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    public static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetLongPathName(string shortPath, char* longPath, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFindHandle FindFirstFile(string fileName, out Win32FindData data);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindClose(nint handle);

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    public static partial int SHFileOperation64(ShFileOpStruct64* op);

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    public static partial int SHFileOperation32(ShFileOpStruct32* op);
}
