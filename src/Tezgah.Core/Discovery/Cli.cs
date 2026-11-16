using System.Diagnostics;
using System.Text;
using Tezgah.Core.Model;

namespace Tezgah.Core.Discovery;

/// <summary>
/// Minimal process runner used by the discovery layer to query external CLIs (git, pipx, npm, cargo, dotnet...).
/// </summary>
/// <remarks>
/// Security rules:
/// <list type="bullet">
/// <item>Never <c>UseShellExecute</c>; arguments are passed one by one through <see cref="ProcessStartInfo.ArgumentList"/>.</item>
/// <item>Executables are resolved against PATH/PATHEXT only (the current directory is never searched).</item>
/// <item><c>.cmd</c>/<c>.bat</c> files (for example <c>npm.cmd</c>) cannot be started without <c>cmd.exe</c>.
/// For those we run <c>%SystemRoot%\System32\cmd.exe /d /s /c ""C:\path\tool.cmd" arg1 arg2"</c>, but ONLY after
/// checking that neither the resolved path nor any argument contains a cmd metacharacter
/// (<c>&amp; | &lt; &gt; ^ % " !</c>, CR or LF). Because no quote can appear inside an argument, wrapping each one in
/// double quotes is unambiguous and nothing can break out into a second command. Anything else is refused.
/// This is the single place where a command line string is assembled, and it is only reachable after that validation.</item>
/// <item>Output is captured as UTF-8, stdin is closed immediately, and on timeout or cancellation the whole process tree is killed.</item>
/// </list>
/// </remarks>
internal static class Cli
{
    private static readonly string[] RunnableExtensions = [".exe", ".com", ".cmd", ".bat"];
    private static readonly System.Buffers.SearchValues<char> CmdMetaCharacters = System.Buffers.SearchValues.Create("&|<>^%\"!\r\n");

    /// <summary>Resolves <paramref name="name"/> to a full path using PATH and PATHEXT. Returns null when not found.</summary>
    public static string? ResolveExecutable(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (Path.IsPathFullyQualified(name))
        {
            return File.Exists(name) ? name : null;
        }

        if (name.IndexOfAny(['\\', '/']) >= 0)
        {
            return null; // relative paths are never resolved (current directory is not trusted)
        }

        var pathExt = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => RunnableExtensions.Contains(e, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (pathExt.Count == 0)
        {
            pathExt.AddRange(RunnableExtensions);
        }

        var hasRunnableExtension = RunnableExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var rawDir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = rawDir.Trim('"');
            if (dir.Length == 0 || !Path.IsPathFullyQualified(dir))
            {
                continue;
            }

            try
            {
                if (hasRunnableExtension)
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    continue;
                }

                foreach (var ext in pathExt)
                {
                    var candidate = Path.Combine(dir, name + ext.ToLowerInvariant());
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch (ArgumentException)
            {
                // invalid characters in a PATH entry - ignore it
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="value"/> can be placed on a cmd.exe command line without being interpreted.</summary>
    public static bool IsSafeForCmd(string value) => value.IndexOfAny(CmdMetaCharacters) < 0;

    /// <summary>
    /// Builds the start info for <paramref name="resolvedPath"/>. Returns null when a batch file would need
    /// unsafe arguments (see class remarks).
    /// </summary>
    internal static ProcessStartInfo? CreateStartInfo(string resolvedPath, IReadOnlyList<string> args, string? workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        var ext = Path.GetExtension(resolvedPath);
        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsSafeForCmd(resolvedPath) || args.Any(a => !IsSafeForCmd(a)))
            {
                return null;
            }

            psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var sb = new StringBuilder("/d /s /c \"");
            sb.Append('"').Append(resolvedPath).Append('"');
            foreach (var a in args)
            {
                sb.Append(' ');
                if (a.Length == 0 || a.Any(c => char.IsWhiteSpace(c) || c is ',' or ';' or '=' or '(' or ')'))
                {
                    sb.Append('"').Append(a).Append('"');
                }
                else
                {
                    sb.Append(a);
                }
            }

            sb.Append('"');
            psi.Arguments = sb.ToString();
            return psi;
        }

        psi.FileName = resolvedPath;
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return psi;
    }

    /// <summary>Runs a tool and captures its output. Never throws for process failures (returns exit code -1 instead); throws only on cancellation.</summary>
    public static async Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null,
        Action<string>? onLine = null)
    {
        var resolved = ResolveExecutable(fileName);
        if (resolved is null)
        {
            return new CommandResult(-1, string.Empty, $"'{fileName}' not found on PATH.", false);
        }

        var psi = CreateStartInfo(resolved, args, workingDirectory);
        if (psi is null)
        {
            return new CommandResult(-1, string.Empty, "Refused: arguments contain characters that are unsafe for a batch file.", false);
        }

        psi.Environment["NO_COLOR"] = "1";
        if (environment is not null)
        {
            foreach (var (k, v) in environment)
            {
                psi.Environment[k] = v;
            }
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stdout)
            {
                stdout.Append(e.Data).Append('\n');
            }

            onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stderr)
            {
                stderr.Append(e.Data).Append('\n');
            }

            onLine?.Invoke(e.Data);
        };

        try
        {
            if (!process.Start())
            {
                return new CommandResult(-1, string.Empty, "Process could not be started.", false);
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new CommandResult(-1, string.Empty, ex.Message, false);
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // process already gone
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        if (!timedOut)
        {
            // Make sure the asynchronous readers drained the pipes.
            process.WaitForExit();
        }

        string o, e;
        lock (stdout)
        {
            o = stdout.ToString();
        }

        lock (stderr)
        {
            e = stderr.ToString();
        }

        return new CommandResult(timedOut ? -1 : process.ExitCode, o, e, timedOut);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
