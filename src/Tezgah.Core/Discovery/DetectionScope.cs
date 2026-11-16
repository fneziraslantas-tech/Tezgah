namespace Tezgah.Core.Discovery;

/// <summary>
/// Ambient cancellation token, time budget and inspected folder for one folder's detection. Detectors are synchronous and
/// their API takes no token, so <see cref="ProjectInspector"/> opens a scope on the calling thread; <see cref="FsUtil.DirectorySize"/>
/// (used for every artifact folder) honours it. This makes Cancel stop a scan promptly and caps the total time spent
/// measuring one project (instead of 8 s per artifact folder, i.e. minutes for a solution with many bin/obj folders).
/// <see cref="FsUtil.HasLink"/> uses <see cref="Root"/> to refuse files reached through a link inside the inspected folder.
/// </summary>
internal static class DetectionScope
{
    [ThreadStatic]
    private static CancellationToken t_token;

    [ThreadStatic]
    private static long t_deadline;

    [ThreadStatic]
    private static string? t_root;

    public static CancellationToken Token => t_token;

    /// <summary>Time left in the current scope, or null outside a scope.</summary>
    public static TimeSpan? Remaining => t_deadline == 0 ? null : TimeSpan.FromTicks(Math.Max(0, t_deadline - DateTime.UtcNow.Ticks));

    /// <summary>Full path of the folder being inspected, or null outside a scope.</summary>
    public static string? Root => t_root;

    public static Scope Enter(TimeSpan budget, string? root, CancellationToken ct)
    {
        var previous = new Scope(t_deadline, t_root, t_token);
        t_token = ct;
        t_deadline = DateTime.UtcNow.Add(budget).Ticks;
        t_root = root is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return previous;
    }

    public readonly struct Scope(long deadline, string? root, CancellationToken token) : IDisposable
    {
        public void Dispose()
        {
            t_token = token;
            t_deadline = deadline;
            t_root = root;
        }
    }
}
