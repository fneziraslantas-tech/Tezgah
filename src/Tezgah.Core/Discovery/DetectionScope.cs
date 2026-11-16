namespace Tezgah.Core.Discovery;

/// <summary>
/// Ambient cancellation token and time budget for one folder's detection. Detectors are synchronous and their API takes
/// no token, so <see cref="ProjectInspector"/> opens a scope on the calling thread; <see cref="FsUtil.DirectorySize"/>
/// (used for every artifact folder) honours it. This makes Cancel stop a scan promptly and caps the total time spent
/// measuring one project (instead of 8 s per artifact folder, i.e. minutes for a solution with many bin/obj folders).
/// </summary>
internal static class DetectionScope
{
    [ThreadStatic]
    private static CancellationToken t_token;

    [ThreadStatic]
    private static long t_deadline;

    public static CancellationToken Token => t_token;

    /// <summary>Time left in the current scope, or null outside a scope.</summary>
    public static TimeSpan? Remaining => t_deadline == 0 ? null : TimeSpan.FromTicks(Math.Max(0, t_deadline - DateTime.UtcNow.Ticks));

    public static Scope Enter(TimeSpan budget, CancellationToken ct)
    {
        var previous = new Scope(t_deadline, t_token);
        t_token = ct;
        t_deadline = DateTime.UtcNow.Add(budget).Ticks;
        return previous;
    }

    public readonly struct Scope(long deadline, CancellationToken token) : IDisposable
    {
        public void Dispose()
        {
            t_token = token;
            t_deadline = deadline;
        }
    }
}
