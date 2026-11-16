using System.IO;
using Tezgah.Core.Model;

namespace Tezgah.App.Demo;

/// <summary>Realistic sample catalog used by demo mode (<c>--demo</c>) and while the real core is not registered.</summary>
public static class DemoData
{
    private const long KB = 1024;
    private const long MB = 1024 * KB;
    private const long GB = 1024 * MB;

    public static string Home { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Repos { get; } = Path.Combine(Home, "source", "repos");

    public static (IReadOnlyList<CatalogItem> Items, IReadOnlyDictionary<string, UserMeta> Meta) Create(DateTimeOffset now)
    {
        var items = new List<CatalogItem>();
        var meta = new Dictionary<string, UserMeta>();

        CatalogItem Repo(string name, string folder, string[] types, string desc, int daysAgo, long size, GitInfo git, RunProfile[] profiles, ArtifactDir[] artifacts)
        {
            var path = Path.Combine(folder, name);
            var item = new CatalogItem
            {
                Id = ItemIds.ForPath(path),
                Kind = ItemKind.GitRepository,
                Source = Sources.FileSystem,
                Name = name,
                Path = path,
                Description = desc,
                LastActivity = now.AddDays(-daysAgo).AddHours(-3),
                InstalledAt = now.AddDays(-daysAgo - 30),
                SizeBytes = size,
                Git = git with { LastCommitAt = now.AddDays(-daysAgo).AddHours(-3) },
                ProjectTypes = types,
                RunProfiles = profiles,
                Artifacts = artifacts,
                WebUrl = null,
            };
            items.Add(item);
            return item;
        }

        // 1. Active node dev server (trusted, favorite)
        var vite = Repo("vite-react-dashboard", Repos, ["node"], "React + Vite admin panel with charts and dark mode.", 2, 412 * MB,
            new GitInfo { RemoteUrl = "https://github.com/furkan/vite-react-dashboard.git", Branch = "main", HasUpstream = true, LastCommitSubject = "feat: add map widget" },
            NodeProfiles(Path.Combine(Repos, "vite-react-dashboard"), "dev"),
            [new ArtifactDir(Path.Combine(Repos, "vite-react-dashboard", "node_modules"), "node_modules", 371 * MB), new ArtifactDir(Path.Combine(Repos, "vite-react-dashboard", "dist"), "dist", 4 * MB)]);
        meta[vite.Id] = new UserMeta { Favorite = true, Trusted = true, Tags = ["web", "iş"], LastRunAt = now.AddDays(-2) };

        // 2. The forgotten repo with unpushed commits
        var whisperPath = Path.Combine(Repos, "whisper-transcriber");
        var whisper = Repo("whisper-transcriber", Repos, ["python"], "Batch-transcribe audio files with OpenAI Whisper, exports SRT.", 142, 1_870 * MB,
            new GitInfo { RemoteUrl = "git@github.com:furkan/whisper-transcriber.git", Branch = "feature/srt-export", HasUpstream = true, Ahead = 3, Behind = 12, ModifiedFiles = 2, UntrackedFiles = 1, Stashes = 1, LastCommitSubject = "wip: srt export" },
            PythonProfiles(whisperPath, "app.py", gradio: true),
            [new ArtifactDir(Path.Combine(whisperPath, ".venv"), "venv", 1_640 * MB), new ArtifactDir(Path.Combine(whisperPath, "__pycache__"), "pycache", 2 * MB)]);
        meta[whisper.Id] = new UserMeta { Note = "Toplantı kayıtları için denemiştim." };

        // 3. Huge forgotten AI repo
        var sdPath = Path.Combine(Home, "AI", "stable-diffusion-webui");
        Repo("stable-diffusion-webui", Path.Combine(Home, "AI"), ["python"], "Stable Diffusion web UI (gradio).", 210, (long)(8.4 * GB),
            new GitInfo { RemoteUrl = "https://github.com/AUTOMATIC1111/stable-diffusion-webui.git", Branch = "master", HasUpstream = true, Behind = 318, LastCommitSubject = "Merge branch 'dev'" },
            PythonProfiles(sdPath, "launch.py", gradio: true),
            [new ArtifactDir(Path.Combine(sdPath, "venv"), "venv", (long)(5.1 * GB)), new ArtifactDir(Path.Combine(sdPath, "repositories"), "cache", 900 * MB)]);

        // 4. This app itself (dotnet)
        var tezgahPath = Path.Combine(Repos, "Tezgah");
        var tezgah = Repo("Tezgah", Repos, ["dotnet"], "Scan, run and remove everything you cloned.", 0, 96 * MB,
            new GitInfo { RemoteUrl = "https://github.com/furkan/Tezgah.git", Branch = "main", HasUpstream = true, ModifiedFiles = 7, LastCommitSubject = "ui: detail pane" },
            DotnetProfiles(tezgahPath, @"src\Tezgah.App\Tezgah.App.csproj"),
            [new ArtifactDir(Path.Combine(tezgahPath, @"src\Tezgah.App\bin"), "bin", 58 * MB), new ArtifactDir(Path.Combine(tezgahPath, @"src\Tezgah.App\obj"), "obj", 21 * MB)]);
        meta[tezgah.Id] = new UserMeta { Favorite = true, Trusted = true };

        // 5. ComfyUI
        var comfyPath = Path.Combine(Home, "AI", "ComfyUI");
        Repo("ComfyUI", Path.Combine(Home, "AI"), ["python"], "Node-based diffusion GUI.", 35, (long)(3.2 * GB),
            new GitInfo { RemoteUrl = "https://github.com/comfyanonymous/ComfyUI.git", Branch = "master", HasUpstream = true, Behind = 41, LastCommitSubject = "Fix LoRA loading" },
            PythonProfiles(comfyPath, "main.py", gradio: false),
            [new ArtifactDir(Path.Combine(comfyPath, ".venv"), "venv", (long)(2.7 * GB))]);

        // 6. Open WebUI (node + docker)
        var owPath = Path.Combine(Repos, "open-webui");
        Repo("open-webui", Repos, ["node", "docker"], "User-friendly AI interface (supports Ollama).", 64, 1_120 * MB,
            new GitInfo { RemoteUrl = "https://github.com/open-webui/open-webui.git", Branch = "main", HasUpstream = true, Behind = 87, LastCommitSubject = "chore: bump deps" },
            [.. NodeProfiles(owPath, "dev"), Profile("docker:compose", "docker compose up", RunProfileKind.Run, "docker", owPath, "docker", "compose", "up")],
            [new ArtifactDir(Path.Combine(owPath, "node_modules"), "node_modules", 980 * MB)]);

        // 7. Rust CLI
        var rgPath = Path.Combine(Repos, "ripgrep");
        Repo("ripgrep", Repos, ["rust"], "Recursively search directories for a regex pattern.", 120, 1_430 * MB,
            new GitInfo { RemoteUrl = "https://github.com/BurntSushi/ripgrep.git", Branch = "master", HasUpstream = true, LastCommitSubject = "release 14.1.1" },
            [
                Profile("cargo:run", "cargo run", RunProfileKind.Run, "rust", rgPath, "cargo", "run", "--", "--version").AsDefault(),
                Profile("cargo:build", "cargo build --release", RunProfileKind.Build, "rust", rgPath, "cargo", "build", "--release"),
                Profile("cargo:test", "cargo test", RunProfileKind.Test, "rust", rgPath, "cargo", "test"),
            ],
            [new ArtifactDir(Path.Combine(rgPath, "target"), "target", 1_380 * MB)]);

        // 8. Go API
        var goPath = Path.Combine(Repos, "go-tile-server");
        Repo("go-tile-server", Repos, ["go"], "Tiny XYZ map tile server for MBTiles.", 18, 24 * MB,
            new GitInfo { RemoteUrl = "https://github.com/furkan/go-tile-server.git", Branch = "main", HasUpstream = false, Ahead = 0, LastCommitSubject = "initial commit" },
            [
                Profile("go:run", "go run .", RunProfileKind.Run, "go", goPath, "go", "run", ".").AsDefault(),
                Profile("go:build", "go build", RunProfileKind.Build, "go", goPath, "go", "build", "./..."),
                Profile("go:test", "go test", RunProfileKind.Test, "go", goPath, "go", "test", "./..."),
            ],
            []);

        // 9. Python GIS scripts (map engineer)
        var gisPath = Path.Combine(Repos, "parsel-sorgu");
        var gis = Repo("parsel-sorgu", Repos, ["python"], "TKGM parsel sorgu ve GeoJSON dışa aktarım betikleri.", 9, 310 * MB,
            new GitInfo { RemoteUrl = "https://github.com/furkan/parsel-sorgu.git", Branch = "main", HasUpstream = true, Ahead = 1, LastCommitSubject = "fix: koordinat dönüşümü" },
            PythonProfiles(gisPath, "main.py", gradio: false),
            [new ArtifactDir(Path.Combine(gisPath, ".venv"), "venv", 290 * MB)]);
        meta[gis.Id] = new UserMeta { Favorite = true, Trusted = true, Tags = ["harita", "cbs"] };

        // 10. Forgotten obsidian plugin without upstream
        var obsPath = Path.Combine(Home, "Desktop", "obsidian-sample-plugin");
        Repo("obsidian-sample-plugin", Path.Combine(Home, "Desktop"), ["node"], "Obsidian plugin template.", 301, 188 * MB,
            new GitInfo { RemoteUrl = "https://github.com/obsidianmd/obsidian-sample-plugin.git", Branch = "master", HasUpstream = true, UntrackedFiles = 4, LastCommitSubject = "Update README" },
            NodeProfiles(obsPath, "dev"),
            [new ArtifactDir(Path.Combine(obsPath, "node_modules"), "node_modules", 181 * MB)]);

        // 11. Extracted zip (no git)
        var zipPath = Path.Combine(Home, "Downloads", "leaflet-demo-main");
        items.Add(new CatalogItem
        {
            Id = ItemIds.ForPath(zipPath),
            Kind = ItemKind.ProjectFolder,
            Source = Sources.FileSystem,
            Name = "leaflet-demo-main",
            Path = zipPath,
            Description = "Static Leaflet map demo (downloaded as ZIP).",
            LastActivity = now.AddDays(-97),
            SizeBytes = 3 * MB,
            ProjectTypes = ["static"],
            RunProfiles = [Profile("static:serve", "python -m http.server", RunProfileKind.Run, "static", zipPath, "python", "-m", "http.server", "8000").AsDefault()],
        });

        // 12-14. Installed apps
        items.Add(App("Visual Studio Code", "Microsoft Corporation", "1.106.1", Path.Combine(Home, "AppData", "Local", "Programs", "Microsoft VS Code"), 412 * MB, now.AddDays(-4), now.AddDays(-400),
            "\"unins000.exe\"", "https://code.visualstudio.com/"));
        items.Add(App("QGIS 3.40.4 'Bratislava'", "QGIS.org", "3.40.4", @"C:\Program Files\QGIS 3.40.4", (long)(2.3 * GB), now.AddDays(-11), now.AddDays(-80),
            "Uninstall-QGIS.exe", "https://qgis.org/"));
        items.Add(App("7-Zip 24.09 (x64)", "Igor Pavlov", "24.09", @"C:\Program Files\7-Zip", 6 * MB, now.AddDays(-160), now.AddDays(-500),
            "Uninstall.exe", "https://www.7-zip.org/"));
        items.Add(App("Microsoft Edge WebView2 Runtime", "Microsoft Corporation", "142.0.3595.65", @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application", 690 * MB, now.AddDays(-1), now.AddDays(-700),
            "setup.exe", null));

        // 15-17. Package tools
        items.Add(Tool(Sources.Pipx, "yt-dlp", "2025.10.22", "Feature-rich command-line audio/video downloader.", Path.Combine(Home, "pipx", "venvs", "yt-dlp"), 38 * MB, now.AddDays(-55), "pipx", "uninstall", "yt-dlp"));
        items.Add(Tool(Sources.NpmGlobal, "http-server", "14.1.1", "Simple zero-configuration command-line http server.", Path.Combine(Home, "AppData", "Roaming", "npm", "node_modules", "http-server"), 2 * MB, now.AddDays(-133), "npm", "uninstall", "-g", "http-server"));
        items.Add(Tool(Sources.DotnetTool, "dotnet-ef", "10.0.0", "Entity Framework Core command-line tools.", Path.Combine(Home, ".dotnet", "tools"), 41 * MB, now.AddDays(-20), "dotnet", "tool", "uninstall", "-g", "dotnet-ef"));

        return (items, meta);
    }

    private static CatalogItem App(string name, string publisher, string version, string path, long size, DateTimeOffset lastUse, DateTimeOffset installed, string uninstaller, string? web) => new()
    {
        Id = ItemIds.ForSource(Sources.Registry, name),
        Kind = ItemKind.InstalledApp,
        Source = Sources.Registry,
        Name = name,
        Path = path,
        Version = version,
        Publisher = publisher,
        SizeBytes = size,
        LastActivity = lastUse,
        InstalledAt = installed,
        WebUrl = web,
        UninstallCommand = new CommandSpec { FileName = Path.Combine(path, uninstaller.Trim('"')), Arguments = ["/S"], WorkingDirectory = path },
    };

    private static CatalogItem Tool(string source, string name, string version, string desc, string path, long size, DateTimeOffset installed, string exe, params string[] uninstallArgs) => new()
    {
        Id = ItemIds.ForSource(source, name),
        Kind = ItemKind.PackageTool,
        Source = source,
        Name = name,
        Version = version,
        Description = desc,
        Path = path,
        SizeBytes = size,
        InstalledAt = installed,
        LastActivity = installed,
        ProjectTypes = [source],
        UninstallCommand = new CommandSpec { FileName = exe, Arguments = uninstallArgs, WorkingDirectory = Home },
    };

    internal static RunProfile Profile(string id, string label, RunProfileKind kind, string origin, string dir, string file, params string[] args) => new()
    {
        Id = id,
        Label = label,
        Kind = kind,
        Origin = origin,
        Command = new CommandSpec { FileName = file, Arguments = args, WorkingDirectory = dir },
    };

    internal static RunProfile AsDefault(this RunProfile profile) => profile with { IsDefault = true };

    internal static RunProfile[] NodeProfiles(string dir, string devScript) =>
    [
        Profile("npm:" + devScript, "npm run " + devScript, RunProfileKind.Run, "node", dir, "npm", "run", devScript).AsDefault(),
        Profile("npm:start", "npm start", RunProfileKind.Run, "node", dir, "npm", "start"),
        Profile("npm:install", "npm install", RunProfileKind.Setup, "node", dir, "npm", "install"),
        Profile("npm:build", "npm run build", RunProfileKind.Build, "node", dir, "npm", "run", "build"),
        Profile("npm:test", "npm test", RunProfileKind.Test, "node", dir, "npm", "test"),
    ];

    internal static RunProfile[] PythonProfiles(string dir, string entry, bool gradio)
    {
        var py = Path.Combine(dir, ".venv", "Scripts", "python.exe");
        return
        [
            Profile("python:" + entry, "python " + entry, RunProfileKind.Run, gradio ? "python-gradio" : "python", dir, py, entry).AsDefault(),
            Profile("python:venv", "python -m venv .venv", RunProfileKind.Setup, "python", dir, "python", "-m", "venv", ".venv"),
            Profile("python:pip", "pip install -r requirements.txt", RunProfileKind.Setup, "python", dir, py, "-m", "pip", "install", "-r", "requirements.txt"),
            Profile("python:pytest", "pytest", RunProfileKind.Test, "python", dir, py, "-m", "pytest"),
        ];
    }

    internal static RunProfile[] DotnetProfiles(string dir, string project) =>
    [
        Profile("dotnet:run", "dotnet run", RunProfileKind.Run, "dotnet", dir, "dotnet", "run", "--project", project).AsDefault(),
        Profile("dotnet:restore", "dotnet restore", RunProfileKind.Setup, "dotnet", dir, "dotnet", "restore"),
        Profile("dotnet:build", "dotnet build", RunProfileKind.Build, "dotnet", dir, "dotnet", "build"),
        Profile("dotnet:test", "dotnet test", RunProfileKind.Test, "dotnet", dir, "dotnet", "test"),
    ];
}
