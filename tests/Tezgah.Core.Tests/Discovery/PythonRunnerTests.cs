using Tezgah.Core.Discovery.Detectors;

namespace Tezgah.Core.Tests.Discovery;

public sealed class PythonRunnerTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("poetry.lock", "poetry")]
    [InlineData("Pipfile", "pipenv")]
    [InlineData("uv.lock", "uv")]
    public void Package_manager_projects_run_inside_their_environment(string lockFile, string runner)
    {
        _dir.File(lockFile, "");
        _dir.File("manage.py", "import django");
        var django = new PythonDetector().Detect(_dir.Path)!.Profiles.Single(p => p.Id == "py:django");
        Assert.Equal(runner, django.Command.FileName);
        Assert.Equal(["run", "python", "manage.py", "runserver"], django.Command.Arguments);
    }

    [Fact]
    public void Poetry_section_without_lock_file_uses_poetry()
    {
        _dir.File("pyproject.toml", "[tool.poetry]\nname = \"x\"\n");
        _dir.File("main.py", "print('hi')");
        var main = new PythonDetector().Detect(_dir.Path)!.Profiles.Single(p => p.Id == "py:main");
        Assert.Equal("poetry", main.Command.FileName);
        Assert.Equal(["run", "python", "main.py"], main.Command.Arguments);
    }

    [Fact]
    public void In_project_venv_still_wins()
    {
        _dir.File("uv.lock", "");
        var py = _dir.File(@".venv\Scripts\python.exe", "MZ");
        _dir.File("app.py", "print('hi')");
        var app = new PythonDetector().Detect(_dir.Path)!.Profiles.Single(p => p.Id == "py:app");
        Assert.Equal(py, app.Command.FileName);
        Assert.Equal(["app.py"], app.Command.Arguments);
    }

    [Fact]
    public void Plain_project_keeps_global_python()
    {
        _dir.File("requirements.txt", "requests");
        _dir.File("main.py", "print('hi')");
        Assert.Equal("python", new PythonDetector().Detect(_dir.Path)!.Profiles.Single(p => p.Id == "py:main").Command.FileName);
    }
}
