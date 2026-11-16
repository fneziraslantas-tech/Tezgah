using System.Collections.Concurrent;
using System.Collections.ObjectModel;
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

    private readonly ConcurrentQueue<OutputLine> _pending = new();
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
            Urls.Add(u);
        }

        UpdateStatus();
    }

    public IRunningProcess Process { get; }

    public Guid RunId => Process.RunId;

    public string Title => Process.Profile.Label;

    public string CommandLine => Process.Profile.Command.Display;

    public int MaxLines { get; set; } = DefaultMaxLines;

    public ObservableCollection<OutputLine> Lines { get; } = [];

    public ObservableCollection<Uri> Urls { get; } = [];

    /// <summary>Raised after a batch was appended (the view scrolls to the end when <see cref="AutoScroll"/> is on).</summary>
    public event EventHandler? LinesAppended;

    /// <summary>UI thread: moves queued output into <see cref="Lines"/>, trims to <see cref="MaxLines"/>, updates state.</summary>
    public void Flush()
    {
        var added = 0;
        while (_pending.TryDequeue(out var line))
        {
            if (_snapshot is not null && _snapshot.Remove(line))
            {
                continue;
            }

            Lines.Add(line);
            added++;
        }

        _snapshot = null;
        var excess = Lines.Count - MaxLines;
        for (var i = 0; i < excess; i++)
        {
            Lines.RemoveAt(0);
        }

        while (_pendingUrls.TryDequeue(out var u))
        {
            if (!Urls.Contains(u))
            {
                Urls.Add(u);
            }
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
    private Task StopAsync() => IsRunning ? _runs.StopAsync(RunId, CancellationToken.None) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanSendInput))]
    private async Task SendInputAsync()
    {
        var text = InputText;
        InputText = string.Empty;
        await Process.SendInputAsync(text, CancellationToken.None);
    }

    private bool CanSendInput() => IsRunning && InputText.Length > 0;

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

    private void OnOutput(object? sender, OutputLine e) => _pending.Enqueue(e);

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
