using System.Diagnostics;

namespace Tezgah.Core.Interop;

/// <summary>
/// A Win32 Job Object configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
/// Every process assigned to it, and every descendant it spawns afterwards, is killed when the job is
/// terminated or when its last handle closes — including when Tezgah itself crashes or is killed,
/// because the OS closes our handles on process exit.
/// JOB_OBJECT_LIMIT_BREAKAWAY_OK is deliberately NOT set, so children cannot escape with CREATE_BREAKAWAY_FROM_JOB.
/// </summary>
internal sealed unsafe class JobObject : IDisposable
{
    private readonly SafeJobHandle _handle;

    public JobObject()
    {
        _handle = NativeMethods.CreateJobObject(0, null);
        if (_handle.IsInvalid)
        {
            throw Win32.LastError("CreateJobObject");
        }

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose },
        };
        if (!NativeMethods.SetInformationJobObject(_handle, NativeMethods.JobObjectExtendedLimitInformationClass, &info, (uint)sizeof(JobObjectExtendedLimitInformation)))
        {
            var error = Win32.LastError("SetInformationJobObject");
            _handle.Dispose();
            throw error;
        }
    }

    /// <summary>
    /// Assigns a started process. The Process API cannot create a process suspended, so there is a short window
    /// between CreateProcess and this call in which the child could spawn a grandchild outside the job.
    /// In practice interpreters (node, python, cmd) take far longer than that to start their first child.
    /// </summary>
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!NativeMethods.AssignProcessToJobObject(_handle, process.SafeHandle))
        {
            throw Win32.LastError("AssignProcessToJobObject");
        }
    }

    /// <summary>Number of processes currently alive in the job, or null when it cannot be queried.</summary>
    public int? ActiveProcesses
    {
        get
        {
            if (_handle.IsClosed || _handle.IsInvalid)
            {
                return null;
            }

            var info = default(JobObjectBasicAccountingInformation);
            return NativeMethods.QueryInformationJobObject(_handle, NativeMethods.JobObjectBasicAccountingInformationClass, &info, (uint)sizeof(JobObjectBasicAccountingInformation), 0)
                ? (int)info.ActiveProcesses
                : null;
        }
    }

    /// <summary>Kills every process in the job. Safe to call repeatedly or after dispose.</summary>
    public void Terminate(uint exitCode = 1)
    {
        if (_handle.IsClosed || _handle.IsInvalid)
        {
            return;
        }

        _ = NativeMethods.TerminateJobObject(_handle, exitCode);
    }

    public void Dispose() => _handle.Dispose();
}

internal static class JobObjectExtensions
{
    /// <summary>
    /// Waits until every process in the job has exited (polling). Used for vendor uninstallers that relaunch
    /// themselves from %TEMP% (NSIS, Inno Setup) and let the first process exit immediately.
    /// </summary>
    public static async Task WaitUntilEmptyAsync(this JobObject job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        while (job.ActiveProcesses is > 0)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }
}
