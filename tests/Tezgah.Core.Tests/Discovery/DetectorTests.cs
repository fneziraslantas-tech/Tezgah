using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Discovery;
using Tezgah.Core.Discovery.Detectors;
using Tezgah.Core.Model;

namespace Tezgah.Core.Tests.Discovery;

public sealed class DetectorTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Node_ScriptsDefaultsAndArtifacts()
    {
        _dir.File("package.json", """
            { "name": "web", "description": "My web app",
              "scripts": { "start": "node s.js", "dev": "vite", "build": "vite build", "test": "vitest", "lint": "eslint" } }
            """);
        _dir.File("pnpm-lock.yaml", "");
        _dir.File(@"node_modules\a\index.js", "12345");
        _dir.Sub("dist");

        var d = new NodeDetector().Detect(_dir.Path)!;
        Assert.Equal("node", d.ProjectType);
        Assert.Equal("My web app", d.Description);
        var def = Assert.Single(d.Profiles, p => p.IsDefault);
        Assert.Equal("pnpm:dev", def.Id);
        Assert.Equal("pnpm", def.Command.FileName);
        Assert.Equal(["run", "dev"], def.Command.Arguments);
        Assert.Equal(_dir.Path, def.Command.WorkingDirectory);
        Assert.Contains(d.Profiles, p => p.Id == "pnpm:install" && p.Kind == RunProfileKind.Setup);
        Assert.Contains(d.Profiles, p => p.Id == "pnpm:build" && p.Kind == RunProfileKind.Build);
        Assert.Contains(d.Profiles, p => p.Id == "pnpm:test" && p.Kind == RunProfileKind.Test);
        Assert.DoesNotContain(d.Profiles, p => p.Id.EndsWith("lint", StringComparison.Ordinal));
        var nm = Assert.Single(d.Artifacts, a => a.Path.EndsWith("node_modules", StringComparison.Ordinal));
        Assert.Equal(5, nm.SizeBytes);
        Assert.Contains(d.Artifacts, a => a.Path.EndsWith("dist", StringComparison.Ordinal));
    }

    [Fact]
    public void Node_StartIsDefaultWithoutDev_AndNpmByDefault()
    {
        _dir.File("package.json", """{ "scripts": { "start": "node ." } }""");
        var d = new NodeDetector().Detect(_dir.Path)!;
        Assert.Equal("npm:start", Assert.Single(d.Profiles, p => p.IsDefault).Id);
    }

    [Fact]
    public void Node_MalformedJsonStillDetected()
    {
        _dir.File("package.json", "{ not json");
        var d = new NodeDetector().Detect(_dir.Path);
        Assert.NotNull(d);
        Assert.DoesNotContain(d!.Profiles, p => p.IsDefault);
    }

    [Fact]
    public void Node_NoPackageJson() => Assert.Null(new NodeDetector().Detect(_dir.Path));

    [Fact]
    public void Python_StreamlitWithVenvAndRequirements()
    {
        _dir.File("requirements.txt", "streamlit\n");
        _dir.File("app.py", "import os\nimport streamlit as st\nst.write('hi')\n");
        var venvPython = _dir.File(@".venv\Scripts\python.exe", "");
        _dir.File(@"__pycache__\x.pyc", "abc");

        var d = new PythonDetector().Detect(_dir.Path)!;
        var def = Assert.Single(d.Profiles, p => p.IsDefault);
        Assert.Equal(venvPython, def.Command.FileName);
        Assert.Equal(["-m", "streamlit", "run", "app.py"], def.Command.Arguments);
        var pip = Assert.Single(d.Profiles, p => p.Id == "py:pip-install");
        Assert.Equal(venvPython, pip.Command.FileName);
        Assert.Equal(["-m", "pip", "install", "-r", "requirements.txt"], pip.Command.Arguments);
        Assert.DoesNotContain(d.Profiles, p => p.Id == "py:venv"); // venv already exists
        Assert.Contains(d.Artifacts, a => a.Path.EndsWith(".venv", StringComparison.Ordinal));
        Assert.Contains(d.Artifacts, a => a.Path.EndsWith("__pycache__", StringComparison.Ordinal));
    }

    [Fact]
    public void Python_DjangoAndVenvSetupWhenMissing()
    {
        _dir.File("manage.py", "import django\n");
        _dir.File("requirements.txt", "django\n");
        var d = new PythonDetector().Detect(_dir.Path)!;
        var def = Assert.Single(d.Profiles, p => p.IsDefault);
        Assert.Equal("python", def.Command.FileName);
        Assert.Equal(["manage.py", "runserver"], def.Command.Arguments);
        var venv = Assert.Single(d.Profiles, p => p.Id == "py:venv");
        Assert.Equal(["-m", "venv", ".venv"], venv.Command.Arguments);
        var pip = Assert.Single(d.Profiles, p => p.Id == "py:pip-install");
        Assert.EndsWith(@".venv\Scripts\python.exe", pip.Command.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void Python_FastApiAndFlaskHeuristics()
    {
        _dir.File("main.py", "from fastapi import FastAPI\napi = FastAPI()\n");
        _dir.File("server.py", "from flask import Flask\napp = Flask(__name__)\n");
        _dir.File("pyproject.toml", "[project]\nname = \"x\"\ndescription = \"An API\"\n");
        var d = new PythonDetector().Detect(_dir.Path)!;
        Assert.Equal("An API", d.Description);
        var main = Assert.Single(d.Profiles, p => p.Id == "py:main");
        Assert.True(main.IsDefault);
        Assert.Equal(["-m", "uvicorn", "main:api", "--reload"], main.Command.Arguments);
        var server = Assert.Single(d.Profiles, p => p.Id == "py:server");
        Assert.Equal(["-m", "flask", "--app", "server", "run"], server.Command.Arguments);
        Assert.Contains(d.Profiles, p => p.Id == "py:pip-install" && p.Command.Arguments.SequenceEqual(new[] { "-m", "pip", "install", "-e", "." }));
    }

    [Fact]
    public void Python_ConsoleScriptsFromPyproject()
    {
        _dir.File("pyproject.toml", "[project]\nname = \"dub\"\n\n[project.scripts]\ndub = \"dub.cli:main\"\n\"dub-web\" = \"dub.web:main\"\n\n[project.urls]\nx = \"y\"\n");
        _dir.File(@".venv\Scripts\python.exe", "");
        var exe = _dir.File(@".venv\Scripts\dub.exe", "");
        var d = new PythonDetector().Detect(_dir.Path)!;
        var def = Assert.Single(d.Profiles, p => p.IsDefault);
        Assert.Equal(exe, def.Command.FileName);
        Assert.Empty(def.Command.Arguments);
        Assert.DoesNotContain(d.Profiles, p => p.Id == "py:script:dub-web"); // launcher not installed, not a uv project
        Assert.Equal(["dub", "dub-web"], PythonDetector.ReadScriptNames(File.ReadAllText(Path.Combine(_dir.Path, "pyproject.toml"))));
    }

    [Fact]
    public void Python_PlainScriptAndNothingForEmptyDir()
    {
        Assert.Null(new PythonDetector().Detect(_dir.Path));
        _dir.File("main.py", "print('hi')\n");
        var d = new PythonDetector().Detect(_dir.Path)!;
        Assert.Equal(["main.py"], Assert.Single(d.Profiles, p => p.IsDefault).Command.Arguments);
    }

    [Fact]
    public void Dotnet_ExeProjectsAndArtifacts()
    {
        _dir.File("App.sln", "");
        _dir.File(@"src\Cli\Cli.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><Description>Command line</Description></PropertyGroup></Project>""");
        _dir.File(@"src\Lib\Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        _dir.File(@"src\Web\Web.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        _dir.File(@"tests\T\T.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""");
        _dir.File(@"src\Cli\bin\Debug\a.dll", "xx");
        _dir.Sub("src", "Cli", "obj");

        var d = new DotnetDetector().Detect(_dir.Path)!;
        Assert.Equal("Command line", d.Description);
        var runs = d.Profiles.Where(p => p.Kind == RunProfileKind.Run).ToList();
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal("dotnet", r.Command.FileName));
        Assert.Contains(runs, r => r.Command.Arguments.SequenceEqual(new[] { "run", "--project", @"src\Cli\Cli.csproj" }));
        Assert.Contains(runs, r => r.Command.Arguments.SequenceEqual(new[] { "run", "--project", @"src\Web\Web.csproj" }));
        Assert.Single(d.Profiles, p => p.IsDefault);
        Assert.Contains(d.Profiles, p => p.Kind == RunProfileKind.Test && p.Command.Arguments.SequenceEqual(new[] { "test", "App.sln" }));
        Assert.Contains(d.Artifacts, a => a.Path.EndsWith(@"Cli\bin", StringComparison.Ordinal) && a.SizeBytes == 2);
        Assert.Contains(d.Artifacts, a => a.Path.EndsWith(@"Cli\obj", StringComparison.Ordinal));
    }

    [Fact]
    public void Rust()
    {
        _dir.File("Cargo.toml", "[package]\nname = \"x\"\ndescription = \"Rusty\"\n");
        _dir.Sub("target");
        var d = new RustDetector().Detect(_dir.Path)!;
        Assert.Equal("Rusty", d.Description);
        Assert.Equal(["run"], Assert.Single(d.Profiles, p => p.IsDefault).Command.Arguments);
        Assert.Single(d.Artifacts);
    }

    [Fact]
    public void Go()
    {
        _dir.File("go.mod", "module x\n");
        _dir.File("main.go", "package main\nfunc main(){}\n");
        var d = new GoDetector().Detect(_dir.Path)!;
        var def = Assert.Single(d.Profiles, p => p.IsDefault);
        Assert.Equal("go", def.Command.FileName);
        Assert.Equal(["run", "."], def.Command.Arguments);
        Assert.Empty(d.Artifacts);
    }

    [Fact]
    public void Java_MavenSpringBootAndGradleWrapper()
    {
        _dir.File("pom.xml", "<project><build><plugins><plugin><artifactId>spring-boot-maven-plugin</artifactId></plugin></plugins></build></project>");
        var d = new JavaDetector().Detect(_dir.Path)!;
        Assert.Equal(["spring-boot:run"], Assert.Single(d.Profiles, p => p.IsDefault).Command.Arguments);
        Assert.Equal("mvn", d.Profiles[0].Command.FileName);

        using var g = new TempDir();
        g.File("build.gradle.kts", "plugins { application }");
        var wrapper = g.File("gradlew.bat", "@echo off");
        var gd = new JavaDetector().Detect(g.Path)!;
        var run = Assert.Single(gd.Profiles, p => p.IsDefault);
        Assert.Equal(wrapper, run.Command.FileName);
        Assert.Equal(["run"], run.Command.Arguments);
    }

    [Fact]
    public void Docker_ComposeAndDockerfile()
    {
        _dir.File("compose.yaml", "services: {}");
        var d = new DockerDetector().Detect(_dir.Path)!;
        Assert.Equal(["compose", "-f", "compose.yaml", "up"], Assert.Single(d.Profiles, p => p.IsDefault).Command.Arguments);
        Assert.Contains(d.Profiles, p => p.Command.Arguments.Contains("down"));

        using var f = new TempDir();
        f.File("Dockerfile", "FROM scratch");
        var fd = new DockerDetector().Detect(f.Path)!;
        Assert.DoesNotContain(fd.Profiles, p => p.IsDefault);
        Assert.Equal(RunProfileKind.Build, Assert.Single(fd.Profiles).Kind);
    }

    [Fact]
    public void StaticWeb()
    {
        var index = _dir.File("index.html", "<html></html>");
        var d = new StaticWebDetector().Detect(_dir.Path)!;
        var p = Assert.Single(d.Profiles);
        Assert.Equal("explorer.exe", p.Command.FileName);
        Assert.Equal([index], p.Command.Arguments);
    }

    [Fact]
    public void Scripts_BatAndPs1()
    {
        var bat = _dir.File("run.bat", "@echo off");
        var ps1 = _dir.File("start.ps1", "Write-Host hi");
        _dir.File("install.bat", "@echo off");
        var d = new ScriptDetector().Detect(_dir.Path)!;
        var def = Assert.Single(d.Profiles, p => p.IsDefault);
        Assert.Equal("cmd.exe", def.Command.FileName);
        Assert.Equal(["/d", "/c", bat], def.Command.Arguments);
        var ps = Assert.Single(d.Profiles, p => p.Id == "script:start.ps1");
        Assert.Contains(ps.Command.FileName, new[] { "pwsh", "powershell.exe" });
        Assert.Equal(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ps1], ps.Command.Arguments);
        Assert.Contains(d.Profiles, p => p.Id == "script:install.bat" && p.Kind == RunProfileKind.Setup);
    }

    [Fact]
    public void Scripts_UnsafePathRefused()
    {
        var weird = _dir.Sub("a&b");
        File.WriteAllText(Path.Combine(weird, "run.bat"), "@echo off");
        Assert.Null(new ScriptDetector().Detect(weird));
    }

    [Fact]
    public void Inspector_CombinesDetectorsReadmeAndSingleDefault()
    {
        _dir.File("package.json", """{ "scripts": { "dev": "vite" } }""");
        _dir.File("index.html", "<html/>");
        _dir.File("run.bat", "@echo off");
        _dir.File("README.md", "# X\n\nDoes X things.\n\n## Usage\n\n```sh\nnpm run dev\nnpx serve .\n```\n");

        var inspector = NewInspector();
        var r = inspector.Detect(_dir.Path, includeReadmeCommands: true);
        Assert.Equal(["scripts", "node"], r.ProjectTypes); // static dropped because other types matched
        Assert.Single(r.Profiles, p => p.IsDefault);
        Assert.True(r.Profiles.Single(p => p.IsDefault).Origin == "scripts");
        var readme = Assert.Single(r.Profiles, p => p.Origin == "readme");
        Assert.Equal("npx", readme.Command.FileName); // "npm run dev" deduplicated against the node detector
        Assert.False(readme.IsDefault);
        Assert.Equal("Does X things.", r.Description);
    }

    internal static ProjectInspector NewInspector(IGitClient? git = null) =>
        new(
            [new ScriptDetector(), new NodeDetector(), new PythonDetector(), new DotnetDetector(), new RustDetector(), new GoDetector(), new JavaDetector(), new DockerDetector(), new StaticWebDetector()],
            git ?? new GitClient(NullLogger<GitClient>.Instance),
            NullLogger<ProjectInspector>.Instance);
}
