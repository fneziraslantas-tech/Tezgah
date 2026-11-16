using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Tezgah.Core.Execution;

namespace Tezgah.Core.Tests.Execution;

public partial class ProcessFinderTests
{
    [Fact]
    public async Task Stopping_a_folder_process_spares_children_whose_image_lives_elsewhere()
    {
        using var dir = new TempDir();
        var app = dir.Sub("tools", "app");
        var shell = Path.Combine(app, "shell.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), shell);

        var psi = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
        using var parent = Process.Start(psi)!;
        Process? child = null;
        try
        {
            Assert.True(await Specs.WaitUntilAsync(() => (child = FindChild(parent.Id, "PING.EXE")) is not null, TimeSpan.FromSeconds(30)));

            var finder = new ProcessFinder();
            var found = Assert.Single(finder.FindUnder(app));
            Assert.True(finder.TryKill(found, app));

            Assert.True(parent.WaitForExit(5000));
            Assert.False(child!.HasExited, "the user's other program (image outside the folder) was killed with the tree");
        }
        finally
        {
            try
            {
                child?.Kill();
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                parent.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            child?.Dispose();
        }
    }

    private static Process? FindChild(int parentId, string imageName)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(imageName)))
        {
            if (ParentOf(p.Id) == parentId)
            {
                return p;
            }

            p.Dispose();
        }

        return null;
    }

    // Native parent-PID lookup. The previous helper started a powershell.exe per candidate process, which on slow CI
    // runners exceeded the 10 s discovery window and made this test flaky.
    private static int? ParentOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var status = NtQueryInformationProcess(p.SafeHandle, 0, out var info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
            return status == 0 ? (int)info.InheritedFromUniqueProcessId : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null; // exited or not accessible
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, out ProcessBasicInformation info, int length, out int returnLength);
}