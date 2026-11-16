using System.Diagnostics;
using Tezgah.Core.Discovery;
using Tezgah.Core.Discovery.Detectors;
using Tezgah.Core.Execution;

namespace Tezgah.Core.Tests.Discovery;

public sealed class ParserRegressionTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Readme_link_row_followed_by_text_is_linear()
    {
        var links = string.Join(' ', Enumerable.Range(0, 30).Select(i => $"[Link{i}](https://example.com/p{i})"));
        var readme = "# T\n\n" + links + " and more text here.\n\nReal description.\n";

        // Warm up JIT and regex construction so the measurement is the parse itself (coverage instrumentation on CI
        // makes the first call slow). Exponential backtracking takes minutes, so 2 s still separates linear from not.
        ReadmeParser.ExtractDescription("# warm\n\n[a](b) text\n");
        var sw = Stopwatch.StartNew();
        var task = Task.Run(() => ReadmeParser.ExtractDescription(readme));
        await task.WaitAsync(TimeSpan.FromSeconds(10)); // throws TimeoutException when it hangs
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
    }

    [Theory]
    [InlineData("[English](a) | [中文](b)", true)]
    [InlineData("[![CI](https://x/badge.svg)](https://x) [![npm](https://y/b.svg)](https://y)", true)]
    [InlineData("![logo](logo.png)", true)]
    [InlineData("[Foo](u) is a tool that does useful things for people", false)]
    public void Link_only_lines(string line, bool expected) => Assert.Equal(expected, ReadmeParser.IsLinkOnlyLine(line));

    [Theory]
    [InlineData("P(old)")]
    [InlineData("a,b")]
    [InlineData("a;b")]
    [InlineData("a=b")]
    public async Task Batch_launcher_in_folder_with_cmd_delimiters_runs_the_real_script(string folder)
    {
        var dir = _dir.Sub(folder);
        File.WriteAllText(Path.Combine(dir, "run.bat"), "@echo off\r\necho REAL-SCRIPT\r\n");
        var stem = folder.Split('(', ',', ';', '=')[0];
        File.WriteAllText(Path.Combine(_dir.Path, stem + ".bat"), "@echo off\r\necho WRONG-SIBLING\r\n");

        var profile = Assert.Single(new ScriptDetector().Detect(dir)!.Profiles);
        Assert.Equal(Path.Combine(dir, "run.bat"), profile.Command.FileName);
        Assert.Empty(profile.Command.Arguments);

        var result = await new CommandExecutor().RunAsync(profile.Command, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Contains("REAL-SCRIPT", result.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("WRONG-SIBLING", result.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a%b")]
    [InlineData("a!b")]
    public void Batch_paths_cmd_cannot_quote_are_not_offered(string folder)
    {
        var dir = _dir.Sub(folder);
        File.WriteAllText(Path.Combine(dir, "run.bat"), "@echo off");
        Assert.Null(new ScriptDetector().Detect(dir));
    }

    [Theory]
    [InlineData("plugins { id 'com.android.application' version '8.5.0' apply false }", false)]
    [InlineData("android {\n  defaultConfig { applicationId \"x.y\" }\n}", false)]
    [InlineData("plugins { alias(libs.plugins.android.application) apply false }", false)]
    [InlineData("// this application is great\nplugins { java }", false)]
    [InlineData("plugins { application }", true)]
    [InlineData("plugins {\n  id 'application'\n}", true)]
    [InlineData("plugins { id(\"application\") }", true)]
    [InlineData("apply plugin: 'application'", true)]
    [InlineData("plugins { java }\napplication {\n  mainClass = 'x.Main'\n}", true)]
    public void Gradle_run_only_for_the_application_plugin(string build, bool expected) =>
        Assert.Equal(expected, GradleBuild.AppliesApplicationPlugin(build));

    [Fact]
    public void Same_named_dotnet_projects_get_distinct_run_profiles()
    {
        const string exe = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>";
        _dir.File(@"src\Server\Server.csproj", exe);
        _dir.File(@"samples\Server\Server.csproj", exe);
        var runs = new DotnetDetector().Detect(_dir.Path)!.Profiles.Where(p => p.Kind == Model.RunProfileKind.Run).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal(2, runs.Select(p => p.Id).Distinct().Count());
        Assert.Contains(runs, p => p.Id == "dotnet:run:server"); // the first keeps the short (backward-compatible) id
        Assert.Single(runs, p => p.IsDefault);
    }

    [Theory]
    [InlineData("https://john@corp.com:ghp_X@github.com/org/repo.git", "https://github.com/org/repo.git")]
    [InlineData("ssh://user:pw@host/repo", "ssh://user@host/repo")]
    [InlineData("https://gitlab.com/x/y.git?private_token=abc", "https://gitlab.com/x/y.git")]
    [InlineData("http://u:p@h/x", "http://h/x")]
    [InlineData("https://oauth2:glpat-x@gitlab.com/a/b.git", "https://gitlab.com/a/b.git")]
    [InlineData("git@github.com:x/y.git", "git@github.com:x/y.git")]
    [InlineData("ssh://git@github.com/x/y.git", "ssh://git@github.com/x/y.git")]
    [InlineData("https://dev.azure.com/org/p/_git/r@x", "https://dev.azure.com/org/p/_git/r@x")]
    public void Sanitize_strips_credentials_from_any_scheme(string remote, string expected) =>
        Assert.Equal(expected, GitUrls.Sanitize(remote));
}
