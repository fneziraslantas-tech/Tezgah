using System.Diagnostics;
using System.Text;
using Tezgah.Core.Model;

namespace Tezgah.Core.Execution;

/// <summary>Executable resolution and <see cref="ProcessStartInfo"/> construction shared by the executor and the process host.</summary>
internal static class ProcessLaunch
{
    /// <summary>Extensions CreateProcess can actually start (directly or, for batch files, via cmd.exe).</summary>
    private static readonly string[] RunnableExtensions = [".exe", ".com", ".cmd", ".bat"];

    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Resolves <paramref name="name"/> to a full path.
    /// <list type="bullet">
    /// <item>Fully qualified → used as-is (PATHEXT applied when it has no extension).</item>
    /// <item>Contains a separator ("./gradlew.bat", "venv\Scripts\python") → resolved against <paramref name="workingDirectory"/>.</item>
    /// <item>Bare name → searched in absolute PATH entries only. Unlike CreateProcess we do NOT look in the current or
    /// working directory first, so a repository cannot shadow the program Tezgah itself starts.</item>
    /// </list>
    /// Only .exe/.com/.cmd/.bat results are returned.
    /// <para>
    /// This covers the first hop only. When the result is a batch shim (npm.cmd, yarn.cmd, pnpm.cmd...), cmd.exe runs it
    /// with the project as current directory and resolves bare names inside it (e.g. "node" when node.exe is not next to
    /// the shim) from that directory first. Tool and uninstaller commands (<see cref="CommandExecutor"/>) and discovery
    /// queries therefore set <c>NoDefaultCurrentDirectoryInExePath=1</c>. Runs of trusted projects do not: the variable is
    /// inherited by the whole tree and would break scripts that call local helpers (gradlew, run.bat) by bare name; the
    /// user already trusted that project's code.
    /// </para>
    /// </summary>
    public static string? Resolve(string name, string? workingDirectory, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || name.Contains('"', StringComparison.Ordinal))
        {
            return null;
        }

        var pathExt = GetPathExt(environment);
        if (Path.IsPathFullyQualified(name))
        {
            return Probe(name, pathExt);
        }

        if (name.Contains('\\', StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal))
        {
            if (workingDirectory is null || !Path.IsPathFullyQualified(workingDirectory) || Path.IsPathRooted(name))
            {
                return null;
            }

            return Probe(Path.GetFullPath(Path.Combine(workingDirectory, name)), pathExt);
        }

        string? pathVariable = null;
        if (environment is not null)
        {
            pathVariable = environment.FirstOrDefault(kv => string.Equals(kv.Key, "PATH", StringComparison.OrdinalIgnoreCase)).Value;
        }

        pathVariable ??= Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var rawDir in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = Environment.ExpandEnvironmentVariables(rawDir.Trim('"'));
            if (!Path.IsPathFullyQualified(dir))
            {
                continue; // relative PATH entries ('.', 'bin') would make resolution depend on the working directory
            }

            string candidate;
            try
            {
                candidate = Path.Combine(dir, name);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (Probe(candidate, pathExt) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Builds the start info. Batch files go through <see cref="CmdArgumentGuard"/>; everything else uses ArgumentList.</summary>
    public static ProcessStartInfo CreateStartInfo(string resolvedPath, CommandSpec spec, bool redirectInput, IEnumerable<KeyValuePair<string, string>>? extraEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ProcessStartInfo psi;
        if (CmdArgumentGuard.IsBatchFile(resolvedPath))
        {
            psi = new ProcessStartInfo(CmdArgumentGuard.CmdPath)
            {
                Arguments = CmdArgumentGuard.BuildCmdArguments(resolvedPath, spec.Arguments),
            };
        }
        else
        {
            psi = new ProcessStartInfo(resolvedPath);
            foreach (var argument in spec.Arguments)
            {
                psi.ArgumentList.Add(argument);
            }
        }

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.ErrorDialog = false;
        psi.WorkingDirectory = spec.WorkingDirectory;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = redirectInput;
        psi.StandardOutputEncoding = Utf8NoBom;
        psi.StandardErrorEncoding = Utf8NoBom;
        if (redirectInput)
        {
            psi.StandardInputEncoding = Utf8NoBom;
        }

        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
            {
                psi.Environment[key] = value;
            }
        }

        foreach (var (key, value) in spec.Environment)
        {
            psi.Environment[key] = value;
        }

        return psi;
    }

    private static string[] GetPathExt(IReadOnlyDictionary<string, string>? environment)
    {
        string? raw = null;
        if (environment is not null)
        {
            raw = environment.FirstOrDefault(kv => string.Equals(kv.Key, "PATHEXT", StringComparison.OrdinalIgnoreCase)).Value;
        }

        raw ??= Environment.GetEnvironmentVariable("PATHEXT");
        var list = (raw ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => RunnableExtensions.Contains(e, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return list.Length > 0 ? list : [".COM", ".EXE", ".BAT", ".CMD"];
    }

    private static string? Probe(string candidate, string[] pathExt)
    {
        var extension = Path.GetExtension(candidate);
        if (extension.Length > 0 && RunnableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }

        foreach (var ext in pathExt)
        {
            var withExt = candidate + ext;
            if (File.Exists(withExt))
            {
                return Path.GetFullPath(withExt);
            }
        }

        return null;
    }
}
