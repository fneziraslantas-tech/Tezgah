using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tezgah.App.Services;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.App.ViewModels;

/// <summary>
/// One run (= one console tab). Output arrives on any thread into a queue and is appended on the UI thread in
/// batches by <see cref="Flush"/> (driven by a ~50 ms timer), so chatty processes cannot flood the dispatcher.
/// </summary>
public sealed partial class RunSessionViewModel : ObservableObject, IDisposable
{
    public const int DefaultMaxLines = 5000;

    /// <summary>Maximum URL chips per run (dev servers that log every absolute request URL).</summary>
    public const int MaxUrls = 10;

    private readonly ConcurrentQueue<OutputLine> _pending = new();
    private readonly HashSet<string> _urlOrigins = new(StringComparer.OrdinalIgnoreCase);
    private int _pendingCount;
    private long _dropped;
    private readonly ConcurrentQueue<Uri> _pendingUrls = new();
    private readonly IRunService _runs;
    private readonly IClipboardService _clipboard;
    private readonly IStrings _s;
    private HashSet<OutputLine>? _snapshot;
    private bool _disposed;

    [ObservableProperty]
    private bool _isRunning = true;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendInputCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public RunSessionViewModel(IRunningProcess process, IRunService runs, IClipboardService clipboard, IStrings strings)
    {
        ArgumentNullException.ThrowIfNull(process);
        Process = process;
        _runs = runs;
        _clipboard = clipboard;
        _s = strings;

        // Subscribe first, then take the snapshot; duplicates between the two are filtered on first flush.
        process.OutputReceived += OnOutput;
        process.UrlDetected += OnUrl;
        var snapshot = process.Output;
        _snapshot = [.. snapshot];
        foreach (var line in snapshot.Skip(Math.Max(0, snapshot.Count - MaxLines)))
        {
            Lines.Add(line);
        }

        foreach (var u in process.DetectedUrls)
        {
            AddUrl(u);
        }

        UpdateStatus();
    }

    public IRunningProcess Process { get; }

    public Guid RunId => Process.RunId;

    public string Title => Process.Profile.Label;

    public string CommandLine => Process.Profile.Command.Display;

    public int MaxLines { get; set; } = DefaultMaxLines;

    public BulkObservableCollection<OutputLine> Lines { get; } = [];

    public ObservableCollection<Uri> Urls { get; } = [];

    /// <summary>Raised after a batch was appended (the view scrolls to the end when <see cref="AutoScroll"/> is on).</summary>
    public event EventHandler? LinesAppended;

    /// <summary>
    /// UI thread: moves queued output into <see cref="Lines"/> keeping at most <see cref="MaxLines"/>. The work per tick is
    /// bounded: at most <see cref="MaxLines"/> lines are taken, older ones are dropped with a "lines skipped" marker, and
    /// large batches replace the collection with a single Reset instead of one notification per added/removed line.
    /// </summary>
    public void Flush()
    {
        // Lines older than the newest MaxLines can never be shown: drop them without touching the UI.
        while (Volatile.Read(ref _pendingCount) > MaxLines && _pending.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _dropped);
        }

        var batch = new List<OutputLine>();
        while (batch.Count < MaxLines && _pending.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _pendingCount);
            if (_snapshot is not null && _snapshot.Remove(line))
            {
                continue;
            }

            batch.Add(line);
        }

        _snapshot = null;
        var skipped = Interlocked.Exchange(ref _dropped, 0);
        if (skipped > 0)
        {
            if (batch.Count >= MaxLines)
            {
                batch.RemoveAt(0);
            }

            batch.Insert(0, new OutputLine(DateTimeOffset.Now, _s.Format("Run_LinesSkipped", skipped), IsError: true));
        }

        var added = batch.Count;
        if (added > 0)
        {
            var excess = Lines.Count + batch.Count - MaxLines;
            if (excess > 0 || batch.Count > 64)
            {
                Lines.ReplaceAll(Lines.Skip(Math.Max(0, excess)).Concat(batch));
            }
            else
            {
                foreach (var line in batch)
                {
                    Lines.Add(line);
                }
            }
        }

        while (_pendingUrls.TryDequeue(out var u))
        {
            AddUrl(u);
        }

        if (IsRunning && Process.HasExited)
        {
            IsRunning = false;
            UpdateStatus();
        }

        if (added > 0)
        {
            LinesAppended?.Invoke(this, EventArgs.Empty);
        }
    }

    public void UpdateStatus() =>
        StatusText = !Process.HasExited
            ? _s.Format("Run_RunningSince", Process.StartedAt.ToLocalTime().ToString("t", Formatting.Culture), Process.ProcessId)
            : _s.Format("Run_ExitedWithCode", Process.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?");

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            await _runs.StopAsync(RunId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            StatusText = _s.Format("Run_StopFailed", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSendInput))]
    private async Task SendInputAsync()
    {
        var text = InputText;
        InputText = string.Empty;
        try
        {
            await Process.SendInputAsync(text, CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            // The process exited between the click and the write: keep what the user typed and say why.
            if (InputText.Length == 0)
            {
                InputText = text;
            }

            if (Process.HasExited)
            {
                IsRunning = false;
            }

            StatusText = _s.Format("Run_InputFailed", ex.Message);
        }
    }

    private bool CanSendInput() => IsRunning && !Process.HasExited && InputText.Length > 0;

    partial void OnIsRunningChanged(bool value) => SendInputCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void Copy()
    {
        var sb = new StringBuilder();
        foreach (var l in Lines)
        {
            sb.AppendLine(l.Text);
        }

        _clipboard.SetText(sb.ToString());
    }

    [RelayCommand]
    private void Clear() => Lines.Clear();

    private void OnOutput(object? sender, OutputLine e)
    {
        _pending.Enqueue(e);

        // Bound memory while the UI lags behind a chatty process: keep the newest lines, count the rest.
        if (Interlocked.Increment(ref _pendingCount) > MaxLines * 2 && _pending.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _dropped);
        }
    }

    private void AddUrl(Uri u)
    {
        if (Urls.Count < MaxUrls && _urlOrigins.Add(u.GetLeftPart(UriPartial.Authority)))
        {
            Urls.Add(u);
        }
    }

    private void OnUrl(object? sender, Uri e) => _pendingUrls.Enqueue(e);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Process.OutputReceived -= OnOutput;
        Process.UrlDetected -= OnUrl;
    }
}
