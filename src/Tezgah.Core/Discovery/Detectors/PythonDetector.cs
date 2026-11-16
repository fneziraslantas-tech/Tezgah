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
        var python = venvPython ?? "python";
        var profiles = new List<RunProfile>();

        if (hasManage)
        {
            profiles.Add(FsUtil.Profile("py:django", "Django runserver", RunProfileKind.Run, ProjectType,
                FsUtil.Spec(python, directory, "manage.py", "runserver"), isDefault: true));
        }

        foreach (var entry in entries.Take(4))
        {
            profiles.Add(EntryProfile(directory, python, entry, isDefault: profiles.Count == 0));
        }

        AddSetupProfiles(directory, venvPython, profiles);

        if (Directory.Exists(Path.Combine(directory, "tests")) || File.Exists(Path.Combine(directory, "pytest.ini")) ||
            File.Exists(Path.Combine(directory, "conftest.py")))
        {
            profiles.Add(FsUtil.Profile("py:pytest", "pytest", RunProfileKind.Test, ProjectType, FsUtil.Spec(python, directory, "-m", "pytest")));
        }

        var artifacts = FsUtil.Artifacts(directory, "python", ".venv", "venv", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", "build", "dist");
        foreach (var eggInfo in FsUtil.TopLevelDirectories(directory, "*.egg-info"))
        {
            artifacts.Add(new ArtifactDir(eggInfo, "python", FsUtil.DirectorySize(eggInfo)));
        }

        return new ProjectDetection(ProjectType, profiles, artifacts, ReadDescription(directory));
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

    private RunProfile EntryProfile(string directory, string python, string entry, bool isDefault)
    {
        var source = FsUtil.ReadHead(Path.Combine(directory, entry)) ?? string.Empty;
        var module = Path.GetFileNameWithoutExtension(entry);
        var id = "py:" + module.ToLowerInvariant();

        if (StreamlitImport().IsMatch(source))
        {
            return FsUtil.Profile(id, $"streamlit run {entry}", RunProfileKind.Run, ProjectType,
                FsUtil.Spec(python, directory, "-m", "streamlit", "run", entry), isDefault);
        }

        var fastApi = FastApiApp().Match(source);
        if (fastApi.Success && !source.Contains("uvicorn.run(", StringComparison.Ordinal))
        {
            var target = $"{module}:{fastApi.Groups["var"].Value}";
            return FsUtil.Profile(id, $"uvicorn {target}", RunProfileKind.Run, ProjectType,
                FsUtil.Spec(python, directory, "-m", "uvicorn", target, "--reload"), isDefault);
        }

        var flask = FlaskApp().Match(source);
        if (flask.Success && !source.Contains(".run(", StringComparison.Ordinal))
        {
            return FsUtil.Profile(id, $"flask --app {module} run", RunProfileKind.Run, ProjectType,
                FsUtil.Spec(python, directory, "-m", "flask", "--app", module, "run"), isDefault);
        }

        // Gradio apps, scripts with __main__ blocks and Flask/FastAPI apps that start themselves.
        return FsUtil.Profile(id, $"python {entry}", RunProfileKind.Run, ProjectType, FsUtil.Spec(python, directory, entry), isDefault);
    }

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
            var m = TomlDescription().Match(pyproject);
            if (m.Success)
            {
                return m.Groups["d"].Value.Trim();
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

    [GeneratedRegex(@"^\s*(import\s+streamlit|from\s+streamlit\s+import)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StreamlitImport();

    [GeneratedRegex(@"^(?<var>[A-Za-z_]\w*)\s*=\s*(?:fastapi\.)?FastAPI\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FastApiApp();

    [GeneratedRegex(@"^(?<var>[A-Za-z_]\w*)\s*=\s*(?:flask\.)?Flask\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FlaskApp();

    [GeneratedRegex(@"^\s*description\s*=\s*[""'](?<d>[^""'\r\n]+)[""']", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TomlDescription();

    [GeneratedRegex(@"\bdescription\s*=\s*[""'](?<d>[^""'\r\n]+)[""']", RegexOptions.CultureInvariant)]
    private static partial Regex SetupDescription();
}
