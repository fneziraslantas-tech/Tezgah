using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.Core.Execution;

/// <summary>
/// Tracks processes the user started from Tezgah. Enforces the workspace-trust model and the path guard before any
/// repository code runs. Disposing (DI container disposal at app shutdown) stops everything.
/// </summary>
internal sealed class RunService(ICatalogService catalog, IPathGuard pathGuard, IProcessHost host) : IRunService, IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(3);

    private readonly Lock _gate = new();
    private readonly List<IRunningProcess> _running = [];

    public event EventHandler? RunningChanged;

    public IReadOnlyList<IRunningProcess> Running
    {
        get
        {
            lock (_gate)
            {
                return [.. _running];
            }
        }
    }

    public async Task<IRunningProcess> StartAsync(string itemId, RunProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        ArgumentNullException.ThrowIfNull(profile);

        if (!catalog.GetMeta(itemId).Trusted)
        {
            throw new UntrustedItemException(itemId);
        }

        // Only profiles the catalog knows for this item (detected or user-defined) can be launched, and the catalog's
        // copy of the command is what runs - a caller cannot smuggle a different command under a known profile id.
        var known = catalog.GetProfiles(itemId).FirstOrDefault(p => p.Id == profile.Id);
        if (known is null)
        {
            throw new InvalidOperationException(L.T(
                $"Bu çalıştırma profili öğeye ait değil: {profile.Label}",
                $"This run profile does not belong to the item: {profile.Label}"));
        }

        var check = pathGuard.Check(known.Command.WorkingDirectory, PathOperation.Execute);
        if (!check.Allowed)
        {
            throw new UnauthorizedAccessException(check.Reason);
        }

        var running = host.Start(itemId, known);
        running.Exited += OnExited;
        lock (_gate)
        {
            _running.Add(running);
        }

        RunningChanged?.Invoke(this, EventArgs.Empty);
        if (running.HasExited)
        {
            OnExited(running, EventArgs.Empty);
        }

        await catalog.UpdateMetaAsync(itemId, m => m with { LastRunAt = DateTimeOffset.Now }, ct).ConfigureAwait(false);
        return running;
    }

    public async Task StopAsync(Guid runId, CancellationToken ct)
    {
        IRunningProcess? target;
        lock (_gate)
        {
            target = _running.FirstOrDefault(r => r.RunId == runId);
        }

        if (target is not null)
        {
            await target.StopAsync(StopGrace, ct).ConfigureAwait(false);
            Remove(target);
        }
    }

    public async Task StopAllAsync(CancellationToken ct)
    {
        var all = Running;
        await Task.WhenAll(all.Select(r => r.StopAsync(StopGrace, ct))).ConfigureAwait(false);
        foreach (var r in all)
        {
            Remove(r);
        }
    }

    public IReadOnlyList<IRunningProcess> ForItem(string itemId)
    {
        lock (_gate)
        {
            return [.. _running.Where(r => r.ItemId == itemId)];
        }
    }

    /// <summary>
    /// Synchronous disposal for containers disposed with <c>Dispose()</c> (a ServiceProvider throws for singletons that are
    /// only IAsyncDisposable). Runs the async path on the thread pool so it never deadlocks on a UI thread.
    /// </summary>
    public void Dispose() => Task.Run(() => DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(10));

    public async ValueTask DisposeAsync()
    {
        IRunningProcess[] all;
        lock (_gate)
        {
            all = [.. _running];
            _running.Clear();
        }

        foreach (var r in all)
        {
            await r.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (sender is not IRunningProcess running)
        {
            return;
        }

        // The root exited: Remove disposes it, which kills leftovers in its job (orphaned watchers). Output, exit code
        // and detected URLs stay readable for the UI after disposal.
        Remove(running);
    }

    private void Remove(IRunningProcess running)
    {
        bool removed;
        lock (_gate)
        {
            removed = _running.Remove(running);
        }

        if (removed)
        {
            running.Exited -= OnExited;
            _ = running.DisposeAsync().AsTask();
            RunningChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
