using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Discovery;
using Tezgah.Core.Model;

namespace Tezgah.Core.Tests.Discovery;

public sealed class DetectionCancellationTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>Simulates the user pressing Cancel while a detector measures a big artifact folder.</summary>
    private sealed class CancellingDetector(CancellationTokenSource cts, string bigFolder) : IProjectDetector
    {
        public string ProjectType => "cancelling";

        public ProjectDetection? Detect(string directory)
        {
            cts.Cancel();
            FsUtil.DirectorySize(bigFolder); // detectors call this without a token
            return null;
        }
    }

    private sealed class RecordingDetector : IProjectDetector
    {
        public bool Called { get; private set; }

        public string ProjectType => "recording";

        public ProjectDetection? Detect(string directory)
        {
            Called = true;
            return null;
        }
    }

    [Fact]
    public void Cancel_stops_artifact_measuring_and_the_remaining_detectors()
    {
        var big = _dir.Sub("node_modules");
        for (var i = 0; i < 300; i++)
        {
            File.WriteAllText(Path.Combine(big, $"f{i}.js"), "x");
        }

        using var cts = new CancellationTokenSource();
        var after = new RecordingDetector();
        var inspector = new ProjectInspector([new CancellingDetector(cts, big), after], new CatalogTrustTestsGit(), NullLogger<ProjectInspector>.Instance);

        Assert.ThrowsAny<OperationCanceledException>(() => inspector.Detect(_dir.Path, includeReadmeCommands: false, cts.Token));
        Assert.False(after.Called);
    }

    [Fact]
    public void Measuring_outside_a_scan_is_unaffected()
    {
        var folder = _dir.Sub("x");
        File.WriteAllText(Path.Combine(folder, "a.txt"), "12345");
        Assert.Equal(5, FsUtil.DirectorySize(folder));
    }

    private sealed class CatalogTrustTestsGit : IGitClient
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(false);

        public Task<GitInfo?> GetInfoAsync(string repoPath, bool fetch, CancellationToken ct) => Task.FromResult<GitInfo?>(null);

        public Task<CommandResult> PullAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();
    }
}
