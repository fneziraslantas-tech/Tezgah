namespace Tezgah.App.Services;

/// <summary>
/// One Tezgah per user session: a named mutex marks the first instance; later launches signal a named
/// event, which the first instance turns into "bring the window to front".
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Tezgah.SingleInstance.6E1B";
    private const string EventName = @"Local\Tezgah.Activate.6E1B";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _registration;

    public SingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        IsFirst = createdNew;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
    }

    public bool IsFirst { get; }

    /// <summary>Called by a second instance: wakes the first one.</summary>
    public void SignalFirstInstance() => _activate.Set();

    /// <summary>Invokes <paramref name="onActivate"/> (on a thread-pool thread) whenever another launch signals.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate.Dispose();
        if (IsFirst)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // not owned by this thread anymore
            }
        }

        _mutex.Dispose();
    }
}
