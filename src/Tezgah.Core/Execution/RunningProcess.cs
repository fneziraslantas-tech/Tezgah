using System.ComponentModel;
using System.Diagnostics;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Interop;
using Tezgah.Core.Model;

namespace Tezgah.Core.Execution;

/// <summary>
/// A long-running process started by Tezgah. The root process and every descendant live in a KILL_ON_JOB_CLOSE Job Object,
/// so <see cref="StopAsync"/>, <see cref="DisposeAsync"/> and even a crash of Tezgah take the whole tree down.
/// </summary>
internal sealed class RunningProcess : IRunningProcess
{
    public const int DefaultBufferLines = 5000;

    /// <summary>Longest line kept (a newline-less stream would otherwise feed megabytes to the ANSI/URL regexes and the UI).</summary>
    public const int MaxLineLength = 16 * 1024;

    /// <summary>Distinct dev-server origins remembered per run.</summary>
    public const int MaxUrls = 10;

    private readonly Process _process;
    private readonly JobObject _job;
    private readonly RingBuffer<OutputLine> _output;
    private readonly Lock _urlGate = new();
    private readonly List<Uri> _urls = [];
    private readonly HashSet<string> _urlKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource _exitedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stdoutEof = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stderrEof = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int? _exitCode;
    private int _disposed;
    private int _finishing;

    private RunningProcess(string itemId, RunProfile profile, Process process, JobObject job, int bufferLines)
    {
        ItemId = itemId;
        Profile = profile;
        _process = process;
        _job = job;
        _output = new RingBuffer<OutputLine>(bufferLines);
        StartedAt = DateTimeOffset.Now;
    }

    public event EventHandler<OutputLine>? OutputReceived;

    public event EventHandler<Uri>? UrlDetected;

    public event EventHandler? Exited;

    public Guid RunId { get; } = Guid.NewGuid();

    public string ItemId { get; }

    public RunProfile Profile { get; }

    public int ProcessId { get; private set; }

    public DateTimeOffset StartedAt { get; }

    public bool HasExited => _exitedTcs.Task.IsCompleted;

    public int? ExitCode => _exitCode;

    public IReadOnlyList<OutputLine> Output => _output.Snapshot();

    public IReadOnlyList<Uri> DetectedUrls
    {
        get
        {
            lock (_urlGate)
            {
                return [.. _urls];
            }
        }
    }

    /// <summary>Task completing when the root process exited and its output was drained (bounded wait).</summary>
    internal Task Completion => _exitedTcs.Task;

    /// <summary>Starts the process and binds it to a new Job Object.</summary>
    public static RunningProcess Start(string itemId, RunProfile profile, ProcessStartInfo startInfo, int bufferLines = DefaultBufferLines)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(startInfo);
        var job = new JobObject();
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var running = new RunningProcess(itemId, profile, process, job, bufferLines);
        process.OutputDataReceived += (_, e) => running.OnData(e.Data, isError: false);
        process.ErrorDataReceived += (_, e) => running.OnData(e.Data, isError: true);
        process.Exited += (_, _) => running.OnExited();
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();
            job.Dispose();
            if (ex is Win32Exception { NativeErrorCode: 740 })
            {
                throw new InvalidOperationException(L.T("Bu komut yönetici izni istiyor.", "This command requires administrator rights."), ex);
            }

            throw;
        }

        // Known, accepted race: Process cannot be created suspended, so a child spawned in the first microseconds
        // (before this call) would not be in the job. Interpreters take far longer than that to spawn anything.
        try
        {
            job.Assign(process);
        }
        catch (Win32Exception)
        {
            // Refuse to run a tree we could not contain.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
            job.Dispose();
            throw;
        }

        running.ProcessId = process.Id;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (process.HasExited)
        {
            running.OnExited();
        }

        return running;
    }

    public async Task SendInputAsync(string line, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (HasExited || Volatile.Read(ref _disposed) != 0)
        {
            throw new InvalidOperationException(L.T("İşlem çalışmıyor.", "The process is not running."));
        }

        await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes stdin (many REPLs/servers exit on EOF), waits up to <paramref name="grace"/>, then terminates the whole job.
    /// GenerateConsoleCtrlEvent is not used: it only works for processes sharing our console, which a windowless GUI app does not have.
    /// </summary>
    public async Task StopAsync(TimeSpan grace, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!HasExited)
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // pipe already broken
            }

            if (grace > TimeSpan.Zero)
            {
                await Task.WhenAny(Completion, Task.Delay(grace, ct)).ConfigureAwait(false);
            }
        }

        // Always terminate the job: even when the root exited by itself, grandchildren (esbuild, workers) may still run.
        _job.Terminate();
        await Task.WhenAny(Completion, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _job.Terminate();

        // Never dispose the Process (which closes the pipes) before the readers delivered the last lines.
        await Task.WhenAny(Task.WhenAll(Completion, _stdoutEof.Task, _stderrEof.Task), Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        _job.Dispose();
        _process.Dispose();
    }

    private void OnData(string? data, bool isError)
    {
        if (data is null)
        {
            (isError ? _stderrEof : _stdoutEof).TrySetResult();
            return;
        }

        if (data.Length > MaxLineLength)
        {
            data = data[..MaxLineLength] + "…";
        }

        var text = OutputText.StripAnsi(data);
        var line = new OutputLine(DateTimeOffset.Now, text, isError);
        _output.Add(line);
        OutputReceived?.Invoke(this, line);

        foreach (var uri in OutputText.FindLocalUrls(text))
        {
            bool added;
            lock (_urlGate)
            {
                // One chip per origin: a server logging every request URL must not grow the list without bound.
                added = _urls.Count < MaxUrls && _urlKeys.Add(uri.GetLeftPart(UriPartial.Authority));
                if (added)
                {
                    _urls.Add(uri);
                }
            }

            if (added)
            {
                UrlDetected?.Invoke(this, uri);
            }
        }
    }

    private void OnExited()
    {
        try
        {
            _exitCode = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }

        if (Interlocked.Exchange(ref _finishing, 1) == 0)
        {
            _ = FinishAsync();
        }
    }

    /// <summary>
    /// Process.Exited does not wait for the redirected streams: the last lines (often the traceback that explains a
    /// non-zero exit) may still be in flight. Wait for both EOFs (bounded); if a grandchild keeps the pipes open, kill the
    /// job so they close, wait briefly once more, then report the exit.
    /// </summary>
    private async Task FinishAsync()
    {
        var drained = Task.WhenAll(_stdoutEof.Task, _stderrEof.Task);
        if (await Task.WhenAny(drained, Task.Delay(TimeSpan.FromMilliseconds(750))).ConfigureAwait(false) != drained)
        {
            _job.Terminate();
            await Task.WhenAny(drained, Task.Delay(TimeSpan.FromMilliseconds(500))).ConfigureAwait(false);
        }

        if (_exitedTcs.TrySetResult())
        {
            Exited?.Invoke(this, EventArgs.Empty);
        }
    }
}
