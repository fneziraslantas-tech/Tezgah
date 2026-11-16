using System.Text.RegularExpressions;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.Core.Discovery.Detectors;

/// <summary>Cargo projects.</summary>
internal sealed partial class RustDetector : IProjectDetector
{
    public string ProjectType => "rust";

    public ProjectDetection? Detect(string directory)
    {
        var cargoToml = FsUtil.ReadText(Path.Combine(directory, "Cargo.toml"));
        if (cargoToml is null)
        {
            return null;
        }

        var hasPackage = cargoToml.Contains("[package]", StringComparison.Ordinal);
        var profiles = new List<RunProfile>();
        if (hasPackage)
        {
            profiles.Add(FsUtil.Profile("cargo:run", "cargo run", RunProfileKind.Run, ProjectType, FsUtil.Spec("cargo", directory, "run"), isDefault: true));
        }

        profiles.Add(FsUtil.Profile("cargo:build", "cargo build --release", RunProfileKind.Build, ProjectType, FsUtil.Spec("cargo", directory, "build", "--release")));
        profiles.Add(FsUtil.Profile("cargo:test", "cargo test", RunProfileKind.Test, ProjectType, FsUtil.Spec("cargo", directory, "test")));

        return new ProjectDetection(ProjectType, profiles, FsUtil.Artifacts(directory, "rust", "target"), ReadDescription(cargoToml));
    }

    private static string? ReadDescription(string cargoToml)
    {
        try
        {
            var m = Description().Match(cargoToml);
            return m.Success ? m.Groups["d"].Value.Trim() : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    // [ \t]*, not \s*: a Multiline ^\s* crosses newlines and is quadratic on newline-only input.
    [GeneratedRegex(@"^[ \t]*description\s*=\s*""(?<d>[^""\r\n]+)""", RegexOptions.Multiline | RegexOptions.CultureInvariant, 250)]
    internal static partial Regex Description();
}

/// <summary>Go modules.</summary>
internal sealed partial class GoDetector : IProjectDetector
{
    public string ProjectType => "go";

    public ProjectDetection? Detect(string directory)
    {
        if (!File.Exists(Path.Combine(directory, "go.mod")))
        {
            return null;
        }

        var profiles = new List<RunProfile>();
        if (HasMainPackage(directory))
        {
            profiles.Add(FsUtil.Profile("go:run", "go run .", RunProfileKind.Run, ProjectType, FsUtil.Spec("go", directory, "run", "."), isDefault: true));
        }

        var cmdDir = Path.Combine(directory, "cmd");
        if (!FsUtil.HasLink(cmdDir) && Directory.Exists(cmdDir)) // never list a linked folder
        {
            foreach (var sub in FsUtil.TopLevelDirectories(cmdDir).Where(HasMainPackage).Take(4))
            {
                var name = Path.GetFileName(sub);
                profiles.Add(FsUtil.Profile($"go:run:{name.ToLowerInvariant()}", $"go run ./cmd/{name}", RunProfileKind.Run, ProjectType,
                    FsUtil.Spec("go", directory, "run", $"./cmd/{name}"), isDefault: profiles.Count == 0));
            }
        }

        profiles.Add(FsUtil.Profile("go:build", "go build ./...", RunProfileKind.Build, ProjectType, FsUtil.Spec("go", directory, "build", "./...")));
        profiles.Add(FsUtil.Profile("go:test", "go test ./...", RunProfileKind.Test, ProjectType, FsUtil.Spec("go", directory, "test", "./...")));
        return new ProjectDetection(ProjectType, profiles, [], null);
    }

    private static bool HasMainPackage(string dir) =>
        FsUtil.TopLevelFiles(dir, "*.go").Take(50).Any(f => IsMainPackage(FsUtil.ReadHead(f, 8192) ?? string.Empty));

    private static bool IsMainPackage(string source)
    {
        try
        {
            return MainPackage().IsMatch(source);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    // [ \t]*, not \s*: a Multiline ^\s* crosses newlines and is quadratic on newline-only input.
    [GeneratedRegex(@"^[ \t]*package\s+main\b", RegexOptions.Multiline | RegexOptions.CultureInvariant, 250)]
    internal static partial Regex MainPackage();
}

/// <summary>Maven and Gradle projects. Wrapper scripts (mvnw.cmd / gradlew.bat) are preferred when present.</summary>
internal sealed class JavaDetector : IProjectDetector
{
    private static readonly string[] GradleFiles = ["build.gradle", "build.gradle.kts"];

    public string ProjectType => "java";

    public ProjectDetection? Detect(string directory)
    {
        var pom = Path.Combine(directory, "pom.xml");
        if (File.Exists(pom))
        {
            var mvnw = Path.Combine(directory, "mvnw.cmd");
            var mvn = File.Exists(mvnw) ? mvnw : "mvn";
            var text = FsUtil.ReadText(pom, 2 * 1024 * 1024) ?? string.Empty;
            var profiles = new List<RunProfile>();
            if (text.Contains("spring-boot-maven-plugin", StringComparison.Ordinal))
            {
                profiles.Add(FsUtil.Profile("mvn:spring-boot-run", "mvn spring-boot:run", RunProfileKind.Run, ProjectType, FsUtil.Spec(mvn, directory, "spring-boot:run"), isDefault: true));
            }
            else if (text.Contains("exec-maven-plugin", StringComparison.Ordinal))
            {
                profiles.Add(FsUtil.Profile("mvn:exec", "mvn exec:java", RunProfileKind.Run, ProjectType, FsUtil.Spec(mvn, directory, "exec:java"), isDefault: true));
            }

            profiles.Add(FsUtil.Profile("mvn:package", "mvn package", RunProfileKind.Build, ProjectType, FsUtil.Spec(mvn, directory, "package")));
            profiles.Add(FsUtil.Profile("mvn:test", "mvn test", RunProfileKind.Test, ProjectType, FsUtil.Spec(mvn, directory, "test")));
            return new ProjectDetection(ProjectType, profiles, FsUtil.Artifacts(directory, "java", "target"), null);
        }

        var gradleFile = GradleFiles.Select(f => Path.Combine(directory, f)).FirstOrDefault(File.Exists);
        if (gradleFile is null)
        {
            return null;
        }

        var gradlew = Path.Combine(directory, "gradlew.bat");
        var gradle = File.Exists(gradlew) ? gradlew : "gradle";
        var build = FsUtil.ReadText(gradleFile, 2 * 1024 * 1024) ?? string.Empty;
        var gp = new List<RunProfile>();
        if (build.Contains("org.springframework.boot", StringComparison.Ordinal))
        {
            gp.Add(FsUtil.Profile("gradle:bootRun", "gradle bootRun", RunProfileKind.Run, ProjectType, FsUtil.Spec(gradle, directory, "bootRun"), isDefault: true));
        }
        else if (GradleBuild.AppliesApplicationPlugin(build))
        {
            gp.Add(FsUtil.Profile("gradle:run", "gradle run", RunProfileKind.Run, ProjectType, FsUtil.Spec(gradle, directory, "run"), isDefault: true));
        }

        gp.Add(FsUtil.Profile("gradle:build", "gradle build", RunProfileKind.Build, ProjectType, FsUtil.Spec(gradle, directory, "build")));
        gp.Add(FsUtil.Profile("gradle:test", "gradle test", RunProfileKind.Test, ProjectType, FsUtil.Spec(gradle, directory, "test")));
        return new ProjectDetection(ProjectType, gp, FsUtil.Artifacts(directory, "java", "build", ".gradle"), null);
    }
}

/// <summary>Docker Compose / Dockerfile projects.</summary>
internal sealed class DockerDetector : IProjectDetector
{
    private static readonly string[] ComposeFiles = ["compose.yaml", "compose.yml", "docker-compose.yml", "docker-compose.yaml"];

    public string ProjectType => "docker";

    public ProjectDetection? Detect(string directory)
    {
        var compose = ComposeFiles.Select(f => Path.Combine(directory, f)).FirstOrDefault(File.Exists);
        if (compose is not null)
        {
            var file = Path.GetFileName(compose);
            return new ProjectDetection(ProjectType,
            [
                FsUtil.Profile("docker:up", "docker compose up", RunProfileKind.Run, ProjectType, FsUtil.Spec("docker", directory, "compose", "-f", file, "up"), isDefault: true),
                FsUtil.Profile("docker:down", "docker compose down", RunProfileKind.Run, ProjectType, FsUtil.Spec("docker", directory, "compose", "-f", file, "down")),
                FsUtil.Profile("docker:build", "docker compose build", RunProfileKind.Build, ProjectType, FsUtil.Spec("docker", directory, "compose", "-f", file, "build")),
            ], [], null);
        }

        if (File.Exists(Path.Combine(directory, "Dockerfile")))
        {
            var tag = ImageTag(Path.GetFileName(directory));
            return new ProjectDetection(ProjectType,
            [
                FsUtil.Profile("docker:image", $"docker build -t {tag} .", RunProfileKind.Build, ProjectType, FsUtil.Spec("docker", directory, "build", "-t", tag, ".")),
            ], [], null);
        }

        return null;
    }

    internal static string ImageTag(string folderName)
    {
        var chars = folderName.ToLowerInvariant().Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' ? c : '-').ToArray();
        var tag = new string(chars).Trim('-', '.', '_');
        return tag.Length == 0 ? "app" : tag;
    }
}

/// <summary>A plain static website (index.html at the root). Dropped by the inspector when another ecosystem matches.</summary>
internal sealed class StaticWebDetector : IProjectDetector
{
    public string ProjectType => "static";

    public ProjectDetection? Detect(string directory)
    {
        var index = Path.Combine(directory, "index.html");
        if (!File.Exists(index))
        {
            return null;
        }

        return new ProjectDetection(ProjectType,
        [
            FsUtil.Profile("static:open", L.T("Tarayıcıda aç", "Open in browser"), RunProfileKind.Run, ProjectType, FsUtil.Spec("explorer.exe", directory, index), isDefault: true),
        ], [], null);
    }
}

/// <summary>Detects the Gradle <c>application</c> plugin (which provides the <c>run</c> task).</summary>
internal static partial class GradleBuild
{
    public static bool AppliesApplicationPlugin(string build)
    {
        ArgumentNullException.ThrowIfNull(build);
        try
        {
            var code = Comments().Replace(build, string.Empty);
            if (code.Contains("com.android.", StringComparison.Ordinal) || AndroidBlock().IsMatch(code))
            {
                return false; // Android: "com.android.application"/"applicationId" are not the application plugin
            }

            return ApplicationPlugin().IsMatch(code);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant, 250)]
    private static partial Regex Comments();

    [GeneratedRegex(@"(?m)^[ \t]*android\s*\{", RegexOptions.CultureInvariant, 250)]
    internal static partial Regex AndroidBlock();

    // id 'application' / id("application") / apply plugin: 'application' / bare `application` in plugins { } / application { } block
    [GeneratedRegex(@"\bid\s*\(?\s*[""']application[""']|apply\s+plugin\s*:\s*[""']application[""']|plugins\s*\{[^}]*?(?<![\w.\-""'])application(?![\w.\-""'])|(?m)^[ \t]*application\s*\{", RegexOptions.CultureInvariant, 250)]
    internal static partial Regex ApplicationPlugin();
}
