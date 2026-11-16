using System.Text.RegularExpressions;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.Core.Discovery.Detectors;

/// <summary>
/// Python projects: pyproject.toml / requirements.txt / setup.py / Pipfile, or well-known entry scripts
/// (main.py, app.py, manage.py, Streamlit/Gradio/Flask/FastAPI apps). Sources are only read, never imported.
/// </summary>
internal sealed partial class PythonDetector : IProjectDetector
{
    private static readonly string[] Markers = ["pyproject.toml", "requirements.txt", "setup.py", "setup.cfg", "Pipfile"];

    /// <summary>Entry scripts in priority order.</summary>
    private static readonly string[] EntryScripts =
    [
        "main.py", "app.py", "streamlit_app.py", "webui.py", "run.py", "server.py", "launch.py", "gui.py", "bot.py", "cli.py", "Home.py",
    ];

    public string ProjectType => "python";

    public ProjectDetection? Detect(string directory)
    {
        var hasMarker = Markers.Any(m => File.Exists(Path.Combine(directory, m)));
        var hasManage = File.Exists(Path.Combine(directory, "manage.py"));
        var entries = EntryScripts.Where(e => File.Exists(Path.Combine(directory, e))).ToList();
        if (!hasMarker && !hasManage && entries.Count == 0)
        {
            return null;
        }

        var venvPython = FindVenvPython(directory);
        var python = ResolveRunner(directory, venvPython);
        var profiles = new List<RunProfile>();

        if (hasManage)
        {
            profiles.Add(FsUtil.Profile("py:django", "Django runserver", RunProfileKind.Run, ProjectType,
                python.Spec(directory, "manage.py", "runserver"), isDefault: true));
        }

        foreach (var entry in entries.Take(4))
        {
            profiles.Add(EntryProfile(directory, python, entry, isDefault: profiles.Count == 0));
        }

        AddConsoleScriptProfiles(directory, venvPython, python, profiles);

        AddSetupProfiles(directory, venvPython, profiles);

        if (Directory.Exists(Path.Combine(directory, "tests")) || File.Exists(Path.Combine(directory, "pytest.ini")) ||
            File.Exists(Path.Combine(directory, "conftest.py")))
        {
            profiles.Add(FsUtil.Profile("py:pytest", "pytest", RunProfileKind.Test, ProjectType, python.Spec(directory, "-m", "pytest")));
        }

        var artifacts = FsUtil.Artifacts(directory, "python", ".venv", "venv", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", "build", "dist");
        foreach (var eggInfo in FsUtil.TopLevelDirectories(directory, "*.egg-info"))
        {
            artifacts.Add(new ArtifactDir(eggInfo, "python", FsUtil.DirectorySize(eggInfo)));
        }

        return new ProjectDetection(ProjectType, profiles, artifacts, ReadDescription(directory));
    }

    /// <summary>How to start the project's interpreter: <c>python</c> plus a prefix (e.g. <c>poetry run</c>).</summary>
    internal sealed record PythonRunner(string FileName, string[] Prefix)
    {
        public CommandSpec Spec(string directory, params string[] args) => FsUtil.Spec(FileName, directory, [.. Prefix, .. args]);
    }

    /// <summary>
    /// The project's own interpreter: an in-project venv wins; otherwise the package manager's environment (uv, Poetry and
    /// pipenv keep their venvs outside the project, so a bare global "python" would never see the installed packages).
    /// </summary>
    internal static PythonRunner ResolveRunner(string directory, string? venvPython)
    {
        if (venvPython is not null)
        {
            return new PythonRunner(venvPython, []);
        }

        if (File.Exists(Path.Combine(directory, "uv.lock")))
        {
            return new PythonRunner("uv", ["run", "python"]);
        }

        if (File.Exists(Path.Combine(directory, "poetry.lock")) ||
            (FsUtil.ReadText(Path.Combine(directory, "pyproject.toml")) is { } pyproject && pyproject.Contains("[tool.poetry]", StringComparison.Ordinal)))
        {
            return new PythonRunner("poetry", ["run", "python"]);
        }

        if (File.Exists(Path.Combine(directory, "Pipfile")))
        {
            return new PythonRunner("pipenv", ["run", "python"]);
        }

        return new PythonRunner("python", []);
    }

    private void AddSetupProfiles(string directory, string? venvPython, List<RunProfile> profiles)
    {
        var hasRequirements = File.Exists(Path.Combine(directory, "requirements.txt"));
        var hasPyproject = File.Exists(Path.Combine(directory, "pyproject.toml"));
        var hasSetupPy = File.Exists(Path.Combine(directory, "setup.py"));

        if (File.Exists(Path.Combine(directory, "uv.lock")))
        {
            profiles.Add(FsUtil.Profile("py:uv-sync", "uv sync", RunProfileKind.Setup, ProjectType, FsUtil.Spec("uv", directory, "sync")));
            return;
        }

        if (File.Exists(Path.Combine(directory, "poetry.lock")))
        {
            profiles.Add(FsUtil.Profile("py:poetry-install", "poetry install", RunProfileKind.Setup, ProjectType, FsUtil.Spec("poetry", directory, "install")));
            return;
        }

        if (File.Exists(Path.Combine(directory, "Pipfile")) && !hasRequirements)
        {
            profiles.Add(FsUtil.Profile("py:pipenv-install", "pipenv install", RunProfileKind.Setup, ProjectType, FsUtil.Spec("pipenv", directory, "install")));
            return;
        }

        if (!hasRequirements && !hasPyproject && !hasSetupPy)
        {
            return;
        }

        var targetPython = venvPython ?? Path.Combine(directory, ".venv", "Scripts", "python.exe");
        if (venvPython is null)
        {
            profiles.Add(FsUtil.Profile("py:venv", "1. python -m venv .venv", RunProfileKind.Setup, ProjectType,
                FsUtil.Spec("python", directory, "-m", "venv", ".venv")));
        }

        var label = venvPython is null ? "2. " : string.Empty;
        profiles.Add(hasRequirements
            ? FsUtil.Profile("py:pip-install", label + "pip install -r requirements.txt", RunProfileKind.Setup, ProjectType,
                FsUtil.Spec(targetPython, directory, "-m", "pip", "install", "-r", "requirements.txt"))
            : FsUtil.Profile("py:pip-install", label + "pip install -e .", RunProfileKind.Setup, ProjectType,
                FsUtil.Spec(targetPython, directory, "-m", "pip", "install", "-e", ".")));
    }

    private RunProfile EntryProfile(string directory, PythonRunner python, string entry, bool isDefault)
    {
        var source = FsUtil.ReadHead(Path.Combine(directory, entry)) ?? string.Empty;
        var module = Path.GetFileNameWithoutExtension(entry);
        var id = "py:" + module.ToLowerInvariant();

        if (ImportsStreamlit(source))
        {
            return FsUtil.Profile(id, $"streamlit run {entry}", RunProfileKind.Run, ProjectType,
                python.Spec(directory, "-m", "streamlit", "run", entry), isDefault);
        }

        var fastApi = FastApiApp().Match(source);
        if (fastApi.Success && !source.Contains("uvicorn.run(", StringComparison.Ordinal))
        {
            var target = $"{module}:{fastApi.Groups["var"].Value}";
            return FsUtil.Profile(id, $"uvicorn {target}", RunProfileKind.Run, ProjectType,
                python.Spec(directory, "-m", "uvicorn", target, "--reload"), isDefault);
        }

        var flask = FlaskApp().Match(source);
        if (flask.Success && !source.Contains(".run(", StringComparison.Ordinal))
        {
            return FsUtil.Profile(id, $"flask --app {module} run", RunProfileKind.Run, ProjectType,
                python.Spec(directory, "-m", "flask", "--app", module, "run"), isDefault);
        }

        // Gradio apps, scripts with __main__ blocks and Flask/FastAPI apps that start themselves.
        return FsUtil.Profile(id, $"python {entry}", RunProfileKind.Run, ProjectType, python.Spec(directory, entry), isDefault);
    }

    /// <summary>
    /// <c>[project.scripts]</c> / <c>[tool.poetry.scripts]</c> entry points: run the installed launcher from the venv
    /// (<c>.venv\Scripts\name.exe</c>) or, for uv projects, <c>uv run name</c>.
    /// </summary>
    private void AddConsoleScriptProfiles(string directory, string? venvPython, PythonRunner runner, List<RunProfile> profiles)
    {
        var pyproject = FsUtil.ReadText(Path.Combine(directory, "pyproject.toml"));
        if (pyproject is null)
        {
            return;
        }

        var scriptsDir = venvPython is null ? null : Path.GetDirectoryName(venvPython);
        var isUv = File.Exists(Path.Combine(directory, "uv.lock"));
        foreach (var name in ReadScriptNames(pyproject).Take(3))
        {
            var id = "py:script:" + name.ToLowerInvariant();
            var exe = scriptsDir is null ? null : Path.Combine(scriptsDir, name + ".exe");
            var isDefault = !profiles.Any(p => p.IsDefault);
            if (exe is not null && File.Exists(exe))
            {
                profiles.Add(FsUtil.Profile(id, name, RunProfileKind.Run, ProjectType, FsUtil.Spec(exe, directory), isDefault));
            }
            else if (isUv)
            {
                profiles.Add(FsUtil.Profile(id, $"uv run {name}", RunProfileKind.Run, ProjectType, FsUtil.Spec("uv", directory, "run", name), isDefault));
            }
            else if (runner.FileName is "poetry" or "pipenv")
            {
                profiles.Add(FsUtil.Profile(id, $"{runner.FileName} run {name}", RunProfileKind.Run, ProjectType, FsUtil.Spec(runner.FileName, directory, "run", name), isDefault));
            }
        }
    }

    internal static List<string> ReadScriptNames(string pyproject)
    {
        var names = new List<string>();
        var inScripts = false;
        foreach (var raw in pyproject.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inScripts = line is "[project.scripts]" or "[tool.poetry.scripts]";
                continue;
            }

            if (!inScripts)
            {
                continue;
            }

            var m = ScriptEntry().Match(line);
            if (m.Success)
            {
                names.Add(m.Groups["name"].Value);
            }
        }

        return names;
    }

    [GeneratedRegex(@"^""?(?<name>[A-Za-z0-9][A-Za-z0-9_.\-]*)""?\s*=", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptEntry();

    /// <summary>Full path of an existing virtual environment interpreter, if any.</summary>
    internal static string? FindVenvPython(string directory)
    {
        foreach (var venv in new[] { ".venv", "venv", "env" })
        {
            var p = Path.Combine(directory, venv, "Scripts", "python.exe");
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    private static string? ReadDescription(string directory)
    {
        var pyproject = FsUtil.ReadText(Path.Combine(directory, "pyproject.toml"));
        if (pyproject is not null)
        {
            try
            {
                var m = TomlDescription().Match(pyproject);
                if (m.Success)
                {
                    return m.Groups["d"].Value.Trim();
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // pathological manifest: treat as "no description"
            }
        }

        var setup = FsUtil.ReadText(Path.Combine(directory, "setup.py"));
        if (setup is not null)
        {
            var m = SetupDescription().Match(setup);
            if (m.Success)
            {
                return m.Groups["d"].Value.Trim();
            }
        }

        return null;
    }

    private static bool ImportsStreamlit(string source)
    {
        try
        {
            return StreamlitImport().IsMatch(source);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    // Indentation is [ \t]*, not \s*: under Multiline a leading \s* also consumes newlines, so every line start re-scans
    // the rest of the input and newline-only files become quadratic (256K '\n' took ~30 s). Same for TomlDescription.
    [GeneratedRegex(@"^[ \t]*(import\s+streamlit|from\s+streamlit\s+import)", RegexOptions.Multiline | RegexOptions.CultureInvariant, 250)]
    internal static partial Regex StreamlitImport();

    [GeneratedRegex(@"^(?<var>[A-Za-z_]\w*)\s*=\s*(?:fastapi\.)?FastAPI\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FastApiApp();

    [GeneratedRegex(@"^(?<var>[A-Za-z_]\w*)\s*=\s*(?:flask\.)?Flask\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FlaskApp();

    [GeneratedRegex(@"^[ \t]*description\s*=\s*[""'](?<d>[^""'\r\n]+)[""']", RegexOptions.Multiline | RegexOptions.CultureInvariant, 250)]
    internal static partial Regex TomlDescription();

    [GeneratedRegex(@"\bdescription\s*=\s*[""'](?<d>[^""'\r\n]+)[""']", RegexOptions.CultureInvariant)]
    private static partial Regex SetupDescription();
}
