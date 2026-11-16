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
        : this(killOnClose: true)
    {
    }

    /// <param name="killOnClose">
    /// False only for vendor uninstallers: processes they leave behind on purpose (a browser with a survey page, an Explorer
    /// restart, a tray helper) must survive Tezgah closing the job. Such jobs are never terminated as a whole.
    /// </param>
    public JobObject(bool killOnClose)
    {
        _handle = NativeMethods.CreateJobObject(0, null);
        if (_handle.IsInvalid)
        {
            throw Win32.LastError("CreateJobObject");
        }

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = killOnClose ? NativeMethods.JobObjectLimitKillOnJobClose : 0 },
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

    /// <summary>Ids of the processes currently in the job, or null when the job cannot be queried.</summary>
    public IReadOnlyList<int>? ProcessIds
    {
        get
        {
            if (_handle.IsClosed || _handle.IsInvalid)
            {
                return null;
            }

            // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORD counters, then ULONG_PTR ids (pointer-aligned, offset 8 on both ABIs).
            const int capacity = 1024;
            var bytes = 8 + (capacity * sizeof(nuint));
            var buffer = new byte[bytes];
            fixed (byte* p = buffer)
            {
                *(uint*)p = capacity;
                if (!NativeMethods.QueryInformationJobObject(_handle, NativeMethods.JobObjectBasicProcessIdListClass, p, (uint)bytes, 0))
                {
                    return null;
                }

                var count = Math.Min(*(uint*)(p + 4), capacity);
                var ids = new List<int>((int)count);
                var list = (nuint*)(p + 8);
                for (var i = 0; i < count; i++)
                {
                    ids.Add((int)list[i]);
                }

                return ids;
            }
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
