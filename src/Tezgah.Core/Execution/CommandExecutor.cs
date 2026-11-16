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
internal sealed class CommandExecutor : ICommandExecutor
{
    /// <summary>Maximum characters kept per stream; the rest is drained and discarded so the child never blocks on a full pipe.</summary>
    internal const int MaxCapturedChars = 1_000_000;

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
        using var process = new Process { StartInfo = psi };
        using var job = new JobObject();
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

        process.StandardInput.Close();
        var stdoutTask = ReadBoundedAsync(process.StandardOutput);
        var stderrTask = ReadBoundedAsync(process.StandardError);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            if (spec.WaitForProcessTree)
            {
                // Relaunched copies (e.g. NSIS %TEMP%\~nsuA.tmp\Au_.exe) are still in the job: wait for them too.
                await job.WaitUntilEmptyAsync(linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            KillTree(process, job);
            if (!timedOut)
            {
                throw;
            }
        }

        // The root (and, in tree mode, everything else) exited; leftovers are killed so pipes close and readers finish.
        var exitCode = timedOut ? -1 : process.ExitCode;
        job.Terminate();
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new CommandResult(exitCode, stdout, stderr, timedOut);
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

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            var room = MaxCapturedChars - builder.Length;
            if (room > 0)
            {
                builder.Append(buffer, 0, Math.Min(room, read));
            }
        }

        return builder.ToString();
    }
}
