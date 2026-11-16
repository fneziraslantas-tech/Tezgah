using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Interop;
using Tezgah.Core.Model;

namespace Tezgah.Core.Execution;

/// <summary>
/// Runs short commands with captured output. No shell (ArgumentList only, batch files through <see cref="CmdArgumentGuard"/>),
/// whole tree bound to a Job Object and killed on timeout/cancel.
/// </summary>
/// <remarks>
/// <see cref="CommandSpec.WaitForProcessTree"/> (vendor uninstallers) is different: the job does not kill on close and is
/// waited on only while an uninstaller engine is alive in it (the uninstaller itself, its relaunched temp copy, msiexec,
/// unins*/un_*/Au_ engines...). Anything else it started on purpose — a browser showing a survey page, an Explorer restart, a
/// tray helper — is left running and never killed, not even on timeout.
/// </remarks>
internal sealed class CommandExecutor : ICommandExecutor
{
    /// <summary>Maximum characters kept per stream; the rest is drained and discarded so the child never blocks on a full pipe.</summary>
    internal const int MaxCapturedChars = 1_000_000;

    /// <summary>How long captured output may keep draining after an uninstaller finished (survivors can hold the pipes).</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    /// <summary>File names of installer/uninstaller engines (NSIS, Inno Setup, InstallShield, WiX burn, MSI).</summary>
    private static readonly string[] EnginePrefixes = ["unins", "un_", "au_", "_iu", "uninst", "setup", "_isdel", "isbew", "idriver"];

    private static readonly HashSet<string> EngineNames = new(StringComparer.OrdinalIgnoreCase) { "msiexec.exe", "uninstall.exe", "uninstaller.exe" };

    public string? ResolveExecutable(string name) => ProcessLaunch.Resolve(name, workingDirectory: null);

    public async Task<CommandResult> RunAsync(CommandSpec spec, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!Directory.Exists(spec.WorkingDirectory))
        {
            throw new DirectoryNotFoundException(L.T($"Çalışma klasörü yok: {spec.WorkingDirectory}", $"Working directory does not exist: {spec.WorkingDirectory}"));
        }

        var resolved = ProcessLaunch.Resolve(spec.FileName, spec.WorkingDirectory, spec.Environment)
            ?? throw new FileNotFoundException(L.T($"Program bulunamadı: {spec.FileName}", $"Executable not found: {spec.FileName}"), spec.FileName);

        // Tool/uninstaller commands never resolve bare program names from their working directory (cmd.exe shims).
        var psi = ProcessLaunch.CreateStartInfo(resolved, spec, redirectInput: true, [new("NoDefaultCurrentDirectoryInExePath", "1")]);
        var treeMode = spec.WaitForProcessTree;
        using var process = new Process { StartInfo = psi };
        using var job = new JobObject(killOnClose: !treeMode);
        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            throw new InvalidOperationException(L.T(
                "Bu komut yönetici izni istiyor. Windows Ayarlar › Uygulamalar üzerinden çalıştırın.",
                "This command requires administrator rights. Run it from Windows Settings › Apps."), ex);
        }

        try
        {
            job.Assign(process);
        }
        catch (Win32Exception)
        {
            // Without the job we can still kill the tree via Process.Kill(true) below; do not fail the command.
        }

        var rootImage = Win32.TryGetProcessImagePath(process.Id) ?? psi.FileName;
        process.StandardInput.Close();
        var stdout = new BoundedCapture();
        var stderr = new BoundedCapture();
        var stdoutTask = stdout.ReadAsync(process.StandardOutput);
        var stderrTask = stderr.ReadAsync(process.StandardError);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            if (treeMode)
            {
                // Relaunched copies (e.g. NSIS %TEMP%\~nsuA.tmp\Au_.exe) are still in the job: wait for them, and only them.
                await WaitWhileEngineAliveAsync(job, rootImage, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            if (treeMode)
            {
                KillEngines(process, job, rootImage);
            }
            else
            {
                KillTree(process, job);
            }

            if (!timedOut)
            {
                throw;
            }
        }

        var exitCode = timedOut ? -1 : process.ExitCode;
        if (!treeMode)
        {
            // The root exited; leftovers are killed so pipes close and readers finish.
            job.Terminate();
            return new CommandResult(exitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false), timedOut);
        }

        // Tree mode: survivors the uninstaller left running on purpose may still hold the inherited pipes. Take what was
        // captured instead of waiting for them (and never kill them to get EOF).
        await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(DrainGrace, CancellationToken.None)).ConfigureAwait(false);
        return new CommandResult(exitCode, stdout.Text, stderr.Text, timedOut);
    }

    /// <summary>Polls the job until no process in it is an uninstaller engine (see <see cref="IsInstallerEngine"/>).</summary>
    private static async Task WaitWhileEngineAliveAsync(JobObject job, string rootImage, CancellationToken ct)
    {
        while (true)
        {
            var ids = job.ProcessIds;
            if (ids is null || !ids.Any(id => IsInstallerEngine(Win32.TryGetProcessImagePath(id), rootImage)))
            {
                return;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True when a process with image <paramref name="image"/> is part of the uninstall itself: the uninstaller's own image,
    /// anything running from %TEMP% (self-relaunched copies), from the uninstaller's own folder (unless that is a Windows
    /// folder), or a well-known engine name. Unknown images count as engines (bounded by the timeout).
    /// </summary>
    internal static bool IsInstallerEngine(string? image, string rootImage)
    {
        if (string.IsNullOrEmpty(image))
        {
            return true;
        }

        if (string.Equals(Normalize(image), Normalize(rootImage), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = Path.GetFileName(image);
        if (EngineNames.Contains(name) || (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            || EnginePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (IsUnder(image, Path.GetTempPath()))
        {
            return true;
        }

        var rootDir = Path.GetDirectoryName(Normalize(rootImage));
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !string.IsNullOrEmpty(rootDir) && !IsUnder(rootDir, windows) && !string.Equals(Normalize(rootDir), Normalize(windows), StringComparison.OrdinalIgnoreCase)
            && IsUnder(image, rootDir);
    }

    private static string Normalize(string path)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return Win32.GetLongPath(full) is { } longPath ? Path.TrimEndingDirectorySeparator(longPath) : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool IsUnder(string path, string directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var dir = Normalize(directory) + Path.DirectorySeparatorChar;
        return Normalize(path).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Timeout/cancel in tree mode: stop the uninstall itself, leave everything else it started alone.</summary>
    private static void KillEngines(Process root, JobObject job, string rootImage)
    {
        TryKill(root);
        foreach (var id in job.ProcessIds ?? [])
        {
            if (!IsInstallerEngine(Win32.TryGetProcessImagePath(id), rootImage))
            {
                continue;
            }

            try
            {
                using var p = Process.GetProcessById(id);
                TryKill(p);
            }
            catch (ArgumentException)
            {
                // already gone
            }
        }
    }

    private static void TryKill(Process p)
    {
        try
        {
            p.Kill(entireProcessTree: false);
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
        catch (Win32Exception)
        {
            // access denied / exiting
        }
    }

    private static void KillTree(Process process, JobObject job)
    {
        job.Terminate();
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
        catch (Win32Exception)
        {
            // access denied / exiting
        }
    }

    /// <summary>Drains a stream, keeping at most <see cref="MaxCapturedChars"/>; the text so far is readable at any time.</summary>
    private sealed class BoundedCapture
    {
        private readonly StringBuilder _builder = new();
        private readonly Lock _gate = new();

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _builder.ToString();
                }
            }
        }

        public async Task<string> ReadAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    lock (_gate)
                    {
                        var room = MaxCapturedChars - _builder.Length;
                        if (room > 0)
                        {
                            _builder.Append(buffer, 0, Math.Min(room, read));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException)
            {
                // tree mode returned while a survivor still held the pipe; the process object has been disposed since
            }

            return Text;
        }
    }
}
