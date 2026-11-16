using System.Diagnostics;
using System.Text.RegularExpressions;
using Tezgah.Core.Discovery.Detectors;

namespace Tezgah.Core.Tests.Discovery;

/// <summary>
/// Multiline detector patterns used to start with <c>^\s*</c>: \s also matches '\n', so every line start re-scanned the
/// rest of the input and a newline-only manifest from an untrusted folder took ~30 s (256K '\n'). Indentation is now
/// <c>[ \t]*</c> and each pattern has a 250 ms match timeout that callers treat as "no match".
/// </summary>
public sealed class DetectorRegexLinearityTests : IDisposable
{
    private const int Size = 256 * 1024;

    // Generous: linear scans of 256K chars take well under a millisecond; the quadratic version took ~30 s.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    public static TheoryData<string> Patterns => new() { "streamlit", "toml-description", "cargo-description", "go-main", "gradle-android", "gradle-application" };

    private static Regex Pattern(string name) => name switch
    {
        "streamlit" => PythonDetector.StreamlitImport(),
        "toml-description" => PythonDetector.TomlDescription(),
        "cargo-description" => RustDetector.Description(),
        "go-main" => GoDetector.MainPackage(),
        "gradle-android" => GradleBuild.AndroidBlock(),
        "gradle-application" => GradleBuild.ApplicationPlugin(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Patterns))]
    public void Pattern_has_a_match_timeout(string name) =>
        Assert.Equal(TimeSpan.FromMilliseconds(250), Pattern(name).MatchTimeout);

    [Theory]
    [MemberData(nameof(Patterns))]
    public void Pattern_is_linear_on_whitespace_only_input(string name)
    {
        var regex = Pattern(name);
        regex.IsMatch("\n\n"); // warm up so the measurement is the scan itself

        foreach (var input in new[] { new string('\n', Size), string.Concat(Enumerable.Repeat(" \t\r\n", Size / 4)) })
        {
            var sw = Stopwatch.StartNew();
            bool matched;
            try
            {
                matched = regex.IsMatch(input);
            }
            catch (RegexMatchTimeoutException)
            {
                Assert.Fail($"{name}: hit the match timeout on {input.Length} whitespace chars (quadratic backtracking)");
                return;
            }

            Assert.False(matched);
            Assert.True(sw.Elapsed < Bound, $"{name}: took {sw.Elapsed}");
        }
    }

    [Theory]
    [InlineData("streamlit", "import os\n    import streamlit as st\n")]
    [InlineData("streamlit", "if True:\n\tfrom streamlit import write\n")]
    [InlineData("toml-description", "[project]\n  description = \"x\"\n")]
    [InlineData("toml-description", "[project]\r\n\tdescription='x'\r\n")]
    [InlineData("cargo-description", "[package]\n    description = \"x\"\n")]
    [InlineData("go-main", "// Command x.\n  package main\n")]
    [InlineData("go-main", "\r\npackage main\r\n")]
    [InlineData("gradle-android", "plugins {}\n  android {\n}")]
    [InlineData("gradle-application", "plugins { java }\n\tapplication {\n}")]
    public void Indented_lines_still_match(string name, string input) => Assert.Matches(Pattern(name), input);

    [Fact]
    public void Indented_manifest_values_are_still_detected()
    {
        _dir.File("pyproject.toml", "[project]\nname = \"x\"\n    description = \"Indented\"\n");
        _dir.File("app.py", "import os\n    import streamlit as st\n");
        var py = new PythonDetector().Detect(_dir.Path)!;
        Assert.Equal("Indented", py.Description);
        Assert.Equal(["-m", "streamlit", "run", "app.py"], Assert.Single(py.Profiles, p => p.IsDefault).Command.Arguments);

        _dir.File("Cargo.toml", "[package]\nname = \"x\"\n    description = \"Rusty\"\n");
        Assert.Equal("Rusty", new RustDetector().Detect(_dir.Path)!.Description);

        _dir.File("go.mod", "module x\n");
        _dir.File("main.go", "// Command x.\n  package main\n");
        Assert.Contains(new GoDetector().Detect(_dir.Path)!.Profiles, p => p.Id == "go:run" && p.IsDefault);
    }

    [Fact]
    public void Newline_only_manifests_are_scanned_quickly()
    {
        var newlines = new string('\n', Size);
        _dir.File("pyproject.toml", newlines);
        _dir.File("app.py", newlines);
        _dir.File("Cargo.toml", newlines);
        _dir.File("go.mod", "module x\n");
        _dir.File("main.go", newlines);

        // Warm up JIT and file I/O on a separate small folder first.
        using (var warm = new TempDir())
        {
            warm.File("pyproject.toml", "\n");
            warm.File("app.py", "\n");
            warm.File("Cargo.toml", "\n");
            warm.File("go.mod", "\n");
            warm.File("main.go", "\n");
            _ = new PythonDetector().Detect(warm.Path);
            _ = new RustDetector().Detect(warm.Path);
            _ = new GoDetector().Detect(warm.Path);
        }

        var sw = Stopwatch.StartNew();
        var py = new PythonDetector().Detect(_dir.Path)!;
        var rust = new RustDetector().Detect(_dir.Path)!;
        var go = new GoDetector().Detect(_dir.Path)!;
        Assert.True(sw.Elapsed < Bound, $"took {sw.Elapsed}");

        Assert.Null(py.Description);
        Assert.Equal(["app.py"], Assert.Single(py.Profiles, p => p.IsDefault).Command.Arguments); // plain script, not streamlit
        Assert.Null(rust.Description);
        Assert.DoesNotContain(go.Profiles, p => p.Id == "go:run");
    }
}
