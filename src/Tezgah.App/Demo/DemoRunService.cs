using System.Text.RegularExpressions;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.App.Demo;

/// <summary>Pretends to run profiles: emits believable output, detects a localhost URL, never starts a real process.</summary>
public sealed class DemoRunService(ICatalogService catalog) : IRunService
{
    private readonly object _gate = new();
    private readonly List<IRunningProcess> _running = [];

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

    public event EventHandler? RunningChanged;

    public async Task<IRunningProcess> StartAsync(string itemId, RunProfile profile, CommandSpec expectedCommand, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(expectedCommand);
        if (!catalog.IsTrusted(itemId))
        {
            throw new UntrustedItemException(itemId);
        }

        // Same rules as the real RunService: only the catalog's copy runs, and only if it is what the user was shown.
        var known = catalog.GetProfiles(itemId).FirstOrDefault(p => p.Id == profile.Id)
            ?? throw new InvalidOperationException(Tezgah.Core.L.T($"Bu çalıştırma profili öğeye ait değil: {profile.Label}", $"This run profile does not belong to the item: {profile.Label}"));
        if (!CommandSpec.IsSameInvocation(known.Command, expectedCommand))
        {
            throw new CommandChangedException(itemId, profile.Id);
        }

        await Task.Delay(120, ct).ConfigureAwait(false);
        var proc = new DemoRunningProcess(itemId, known);
        proc.Exited += (_, _) =>
        {
            lock (_gate)
            {
                _running.Remove(proc);
            }

            RunningChanged?.Invoke(this, EventArgs.Empty);
        };
        lock (_gate)
        {
            _running.Add(proc);
        }

        _ = catalog.UpdateMetaAsync(itemId, m => m with { LastRunAt = DateTimeOffset.Now }, CancellationToken.None);
        RunningChanged?.Invoke(this, EventArgs.Empty);
        proc.Begin();
        return proc;
    }

    public Task StopAsync(Guid runId, CancellationToken ct)
    {
        var proc = Running.FirstOrDefault(p => p.RunId == runId);
        return proc is null ? Task.CompletedTask : proc.StopAsync(TimeSpan.FromSeconds(2), ct);
    }

    public async Task StopAllAsync(CancellationToken ct)
    {
        foreach (var p in Running)
        {
            await p.StopAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<IRunningProcess> ForItem(string itemId) => [.. Running.Where(p => p.ItemId == itemId)];
}

public sealed partial class DemoRunningProcess : IRunningProcess
{
    private readonly object _gate = new();
    private readonly List<OutputLine> _output = [];
    private readonly List<Uri> _urls = [];
    private readonly CancellationTokenSource _cts = new();
    private int _exited;

    public DemoRunningProcess(string itemId, RunProfile profile)
    {
        ItemId = itemId;
        Profile = profile;
        ProcessId = Random.Shared.Next(4000, 60000);
    }

    public Guid RunId { get; } = Guid.NewGuid();

    public string ItemId { get; }

    public RunProfile Profile { get; }

    public int ProcessId { get; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public bool HasExited => Volatile.Read(ref _exited) == 1;

    public int? ExitCode { get; private set; }

    public IReadOnlyList<OutputLine> Output
    {
        get
        {
            lock (_gate)
            {
                return [.. _output];
            }
        }
    }

    public IReadOnlyList<Uri> DetectedUrls
    {
        get
        {
            lock (_gate)
            {
                return [.. _urls];
            }
        }
    }

    public event EventHandler<OutputLine>? OutputReceived;

    public event EventHandler<Uri>? UrlDetected;

    public event EventHandler? Exited;

    internal void Begin() => _ = Task.Run(() => ScriptAsync(_cts.Token));

    public Task SendInputAsync(string line, CancellationToken ct)
    {
        Emit("> " + line, false);
        Emit(line.Equals("help", StringComparison.OrdinalIgnoreCase) ? "(demo) press Ctrl+. to stop" : $"(demo) received: {line}", false);
        return Task.CompletedTask;
    }

    public async Task StopAsync(TimeSpan grace, CancellationToken ct)
    {
        if (HasExited)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        Emit("^C", false);
        await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
        Finish(-1073741510); // STATUS_CONTROL_C_EXIT
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task ScriptAsync(CancellationToken ct)
    {
        try
        {
            var (lines, stays) = Script(Profile);
            foreach (var (delay, text, err) in lines)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                Emit(text, err);
            }

            if (!stays)
            {
                Finish(0);
                return;
            }

            var n = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2500, ct).ConfigureAwait(false);
                n++;
                Emit(Profile.Origin.StartsWith("python", StringComparison.Ordinal)
                    ? $"INFO:     127.0.0.1:5{n % 10}312 - \"GET /queue/status HTTP/1.1\" 200 OK"
                    : $"{DateTime.Now:HH:mm:ss} [vite] hmr update /src/App.tsx (x{n})", false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    private static (List<(int Delay, string Text, bool Err)> Lines, bool StaysRunning) Script(RunProfile profile)
    {
        var cmd = profile.Command.Display;
        var lines = new List<(int, string, bool)> { (50, $"$ {cmd}", false) };
        switch (profile.Kind)
        {
            case RunProfileKind.Run when profile.Origin == "node":
                lines.Add((300, "", false));
                lines.Add((100, $"> {profile.Label.Replace("npm run ", string.Empty, StringComparison.Ordinal)}", false));
                lines.Add((100, "> vite --host 127.0.0.1", false));
                lines.Add((600, "", false));
                lines.Add((50, "  VITE v6.3.5  ready in 412 ms", false));
                lines.Add((50, "", false));
                lines.Add((50, "  ➜  Local:   http://localhost:5173/", false));
                lines.Add((50, "  ➜  Network: use --host to expose", false));
                lines.Add((50, "  ➜  press h + enter to show help", false));
                return (lines, true);
            case RunProfileKind.Run when profile.Origin.StartsWith("python", StringComparison.Ordinal):
                lines.Add((500, "Loading model 'base'...", false));
                lines.Add((900, "UserWarning: FP16 is not supported on CPU; using FP32 instead", true));
                lines.Add((300, "Running on local URL:  http://127.0.0.1:7860", false));
                lines.Add((50, "To create a public link, set `share=True` in `launch()`.", false));
                return (lines, true);
            case RunProfileKind.Run when profile.Origin == "dotnet":
                lines.Add((700, "Building...", false));
                lines.Add((1200, "info: Microsoft.Hosting.Lifetime[14]", false));
                lines.Add((50, "      Now listening on: http://localhost:5080", false));
                lines.Add((50, "info: Microsoft.Hosting.Lifetime[0]", false));
                lines.Add((50, "      Application started. Press Ctrl+C to shut down.", false));
                return (lines, true);
            case RunProfileKind.Run:
                lines.Add((400, "Serving HTTP on 127.0.0.1 port 8000 (http://127.0.0.1:8000/) ...", false));
                return (lines, true);
            case RunProfileKind.Setup:
                lines.Add((600, "Resolving dependencies...", false));
                lines.Add((900, "npm warn deprecated inflight@1.0.6: This module is not supported", true));
                lines.Add((900, "added 312 packages, and audited 313 packages in 3s", false));
                lines.Add((100, "found 0 vulnerabilities", false));
                return (lines, false);
            case RunProfileKind.Build:
                lines.Add((700, "Compiling...", false));
                lines.Add((1200, "Build succeeded.", false));
                lines.Add((50, "    0 Warning(s)", false));
                lines.Add((50, "    0 Error(s)", false));
                return (lines, false);
            default:
                lines.Add((600, "Running 24 tests...", false));
                lines.Add((1200, "Passed!  - Failed: 0, Passed: 24, Skipped: 0, Total: 24", false));
                return (lines, false);
        }
    }

    private void Emit(string text, bool isError)
    {
        var line = new OutputLine(DateTimeOffset.Now, text, isError);
        Uri? newUrl = null;
        lock (_gate)
        {
            _output.Add(line);
            var m = UrlRegex().Match(text);
            if (m.Success && Uri.TryCreate(m.Value.TrimEnd('/', ')', '.'), UriKind.Absolute, out var uri) && !_urls.Contains(uri))
            {
                _urls.Add(uri);
                newUrl = uri;
            }
        }

        OutputReceived?.Invoke(this, line);
        if (newUrl is not null)
        {
            UrlDetected?.Invoke(this, newUrl);
        }
    }

    private void Finish(int exitCode)
    {
        if (Interlocked.Exchange(ref _exited, 1) == 1)
        {
            return;
        }

        ExitCode = exitCode;
        Emit(exitCode == 0 ? "Process exited with code 0." : $"Process exited with code {exitCode}.", exitCode != 0);
        Exited?.Invoke(this, EventArgs.Empty);
    }

    [GeneratedRegex(@"https?://(localhost|127\.0\.0\.1|\[::1\]):\d{2,5}[^\s]*", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}
