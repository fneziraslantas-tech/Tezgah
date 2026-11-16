using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Catalog;
using Tezgah.Core.Discovery;
using Tezgah.Core.Discovery.Detectors;
using Tezgah.Core.Model;

namespace Tezgah.Core.Tests.Discovery;

/// <summary>
/// The passive (untrusted) scan never follows a link in a scanned folder: a <c>package.json</c> symlink or a <c>.venv</c>
/// junction could point at <c>\\attacker\share</c>, and opening it would send the user's NTLM credentials before any
/// trust prompt. Links here always target local temp folders; the tests only check that they are not followed.
/// </summary>
public sealed class ScanLinkTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _outside = new();

    public void Dispose()
    {
        _dir.Dispose();
        _outside.Dispose();
    }

    /// <summary>File symlinks need Developer Mode or the symlink privilege; false (test skipped) when unavailable.</summary>
    private static bool TryCreateFileSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return File.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void ReadText_and_ReadHead_refuse_a_file_symlink()
    {
        var target = _outside.File("p.json", """{ "name": "elsewhere" }""");
        var project = _dir.Sub("proj");
        var link = Path.Combine(project, "package.json");
        if (!TryCreateFileSymlink(link, target))
        {
            return; // no symlink privilege on this machine: nothing to verify
        }

        Assert.Null(FsUtil.ReadText(link));
        Assert.Null(FsUtil.ReadHead(link));
        Assert.True(FsUtil.HasLink(link));
        Assert.Null(new NodeDetector().Detect(project)); // the linked manifest is not read

        var plain = _dir.File(@"plain\package.json", "{}");
        Assert.Equal("{}", FsUtil.ReadText(plain));
        Assert.False(FsUtil.HasLink(plain));
    }

    [Fact]
    public void Files_reached_through_a_junction_inside_the_inspected_folder_are_not_read()
    {
        _outside.File(@"src\main.py", "import flask\n");
        var project = _dir.Sub("proj");
        var junction = Path.Combine(project, "src");
        if (!TestTools.TryCreateJunction(junction, Path.Combine(_outside.Path, "src")))
        {
            return; // junctions not supported here: skip
        }

        var viaLink = Path.Combine(junction, "main.py");
        using (DetectionScope.Enter(TimeSpan.FromMinutes(1), project, CancellationToken.None))
        {
            Assert.True(FsUtil.HasLink(viaLink));
            Assert.Null(FsUtil.ReadText(viaLink));
            Assert.Null(FsUtil.ReadHead(viaLink));
        }

        Assert.True(FsUtil.HasLink(viaLink, project));
        Assert.False(FsUtil.FileExists(viaLink, project));
        Assert.False(FsUtil.HasLink(Path.Combine(_outside.Path, "src", "main.py"), _outside.Path));
    }

    [Fact]
    public void Venv_junction_is_not_probed_for_an_interpreter()
    {
        _outside.File(@"venv\Scripts\python.exe", string.Empty);
        var project = _dir.Sub("linked");
        if (!TestTools.TryCreateJunction(Path.Combine(project, ".venv"), Path.Combine(_outside.Path, "venv")))
        {
            return;
        }

        Assert.Null(PythonDetector.FindVenvPython(project));

        var real = _dir.Sub("real");
        _dir.File(@"real\.venv\Scripts\python.exe", string.Empty);
        Assert.Equal(Path.Combine(real, ".venv", "Scripts", "python.exe"), PythonDetector.FindVenvPython(real));
    }

    [Fact]
    public void Inspector_ignores_a_linked_readme_and_manifest()
    {
        var readme = _outside.File("README.md", "# Elsewhere\n\nRead from the link target.\n");
        var pkg = _outside.File("package.json", """{ "name": "elsewhere", "scripts": { "start": "node x.js" } }""");
        var project = _dir.Sub("proj");
        if (!TryCreateFileSymlink(Path.Combine(project, "README.md"), readme) || !TryCreateFileSymlink(Path.Combine(project, "package.json"), pkg))
        {
            return;
        }

        var detection = DetectorTests.NewInspector().Detect(project, includeReadmeCommands: true);
        Assert.Empty(detection.ProjectTypes);
        Assert.Null(detection.Description);
    }

    [Fact]
    public async Task Walker_does_not_count_a_file_symlink_as_a_project_marker()
    {
        var target = _outside.File("package.json", "{}");
        var linked = _dir.Sub("linked");
        if (!TryCreateFileSymlink(Path.Combine(linked, "package.json"), target))
        {
            return;
        }

        _dir.File(@"real\package.json", "{}");
        var provider = new FileSystemProvider(DetectorTests.NewInspector(), NullLogger<FileSystemProvider>.Instance);
        var settings = new TezgahSettings { ScanRoots = [_dir.Path], MaxDepth = 4 };
        var candidates = await provider.FindCandidatesAsync([_dir.Path], settings, null, CancellationToken.None);
        Assert.Equal(["real"], candidates.Select(c => Path.GetRelativePath(_dir.Path, c)));
    }

    [Fact]
    public void Clone_checks_symlinks_out_as_plain_files()
    {
        var args = CatalogService.CloneArguments("https://example.com/x.git", @"C:\src\x");
        var clone = Array.IndexOf(args, "clone");
        var option = Array.IndexOf(args, "core.symlinks=false");
        Assert.True(clone >= 0 && option > clone && args[option - 1] == "-c", string.Join(' ', args));
        Assert.True(option < Array.IndexOf(args, "--"));
    }
}
