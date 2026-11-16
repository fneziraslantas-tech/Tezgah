using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using Tezgah.App.ViewModels;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.App.Tests;

public class RunSessionTests
{
    private sealed class ScriptedProcess : IRunningProcess
    {
        public Guid RunId { get; } = Guid.NewGuid();

        public string ItemId => "item";

        public RunProfile Profile { get; } = new()
        {
            Id = "p", Label = "p", Kind = RunProfileKind.Run, Origin = "test",
            Command = new CommandSpec { FileName = "x", WorkingDirectory = "C:\\" },
        };

        public int ProcessId => 1;

        public DateTimeOffset StartedAt => DateTimeOffset.Now;

        public bool HasExited { get; set; }

        public int? ExitCode => HasExited ? 0 : null;

        public IReadOnlyList<OutputLine> Output => [];

        public IReadOnlyList<Uri> DetectedUrls => [];

        public Exception? SendError { get; set; }

        public event EventHandler<OutputLine>? OutputReceived;

        public event EventHandler<Uri>? UrlDetected;

        public event EventHandler? Exited;

        public void Emit(string text) => OutputReceived?.Invoke(this, new OutputLine(DateTimeOffset.Now, text, false));

        public void EmitUrl(Uri u) => UrlDetected?.Invoke(this, u);

        public void Exit()
        {
            HasExited = true;
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public Task SendInputAsync(string line, CancellationToken ct) => SendError is null ? Task.CompletedTask : Task.FromException(SendError);

        public Task StopAsync(TimeSpan grace, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static RunSessionViewModel Session(ScriptedProcess p) =>
        new(p, new Demo.DemoRunService(new Demo.DemoCatalogService(new Demo.DemoOptions(true))), new FakeClipboard(), new KeyStrings());

    [Fact]
    public void A_flood_of_output_is_bounded_per_flush_and_in_memory()
    {
        var p = new ScriptedProcess();
        var s = Session(p);
        for (var i = 0; i < 200_000; i++)
        {
            p.Emit("line " + i);
        }

        var notifications = 0;
        s.Lines.CollectionChanged += (_, _) => notifications++;
        var sw = Stopwatch.StartNew();
        s.Flush();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Flush took {sw.Elapsed}");
        Assert.True(s.Lines.Count <= s.MaxLines);
        Assert.Equal("line 199999", s.Lines[^1].Text);
        Assert.Contains(s.Lines, l => l.Text.StartsWith("Run_LinesSkipped(", StringComparison.Ordinal));
        Assert.True(notifications <= 2, $"{notifications} collection notifications");
    }

    [Fact]
    public void Small_batches_keep_incremental_adds_and_trim_to_max()
    {
        var p = new ScriptedProcess();
        var s = Session(p);
        s.MaxLines = 10;
        for (var i = 0; i < 25; i++)
        {
            p.Emit("l" + i);
            s.Flush();
        }

        Assert.Equal(10, s.Lines.Count);
        Assert.Equal("l24", s.Lines[^1].Text);
    }

    [Fact]
    public void Urls_are_one_per_origin_and_capped()
    {
        var p = new ScriptedProcess();
        var s = Session(p);
        for (var i = 0; i < 1000; i++)
        {
            p.EmitUrl(new Uri($"http://localhost:3000/x?id={i}"));
        }

        for (var port = 4000; port < 4030; port++)
        {
            p.EmitUrl(new Uri($"http://localhost:{port}/"));
        }

        s.Flush();
        Assert.Equal(RunSessionViewModel.MaxUrls, s.Urls.Count);
        Assert.Single(s.Urls, u => u.Port == 3000);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(ObjectDisposedException))]
    public async Task Send_input_racing_with_exit_keeps_the_text_and_does_not_crash(Type errorType)
    {
        var p = new ScriptedProcess { SendError = errorType == typeof(ObjectDisposedException) ? new ObjectDisposedException("stdin") : (Exception)Activator.CreateInstance(errorType, "gone")! };
        var s = Session(p);
        s.InputText = "y";
        p.HasExited = true;

        await s.SendInputCommand.ExecuteAsync(null);

        Assert.Equal("y", s.InputText);
        Assert.StartsWith("Run_InputFailed(", s.StatusText, StringComparison.Ordinal);
        Assert.False(s.SendInputCommand.CanExecute(null));
    }
}
