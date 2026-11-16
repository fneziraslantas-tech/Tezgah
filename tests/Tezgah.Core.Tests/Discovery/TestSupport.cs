using System.Diagnostics;

namespace Tezgah.Core.Tests.Discovery;

/// <summary>A unique temp directory removed on dispose. Never touches %LOCALAPPDATA%\Tezgah.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tezgah-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(params string[] parts)
    {
        var p = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    public string File(string relative, string content = "")
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public void Dispose()
    {
        try
        {
            // Remove junctions first so deleting never recurses into their targets.
            foreach (var d in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).ToList())
            {
                if (Directory.Exists(d) && (System.IO.File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(d);
                }
            }

            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                System.IO.File.SetAttributes(f, FileAttributes.Normal); // git objects are read-only
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class TestTools
{
    private static readonly Lazy<bool> GitAvailable = new(() => Run("git", null, "--version") == 0);

    public static bool HasGit => GitAvailable.Value;

    public static int Run(string file, string? workingDirectory, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory ?? System.IO.Path.GetTempPath(),
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000))
            {
                p.Kill(true);
                return -1;
            }

            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }

    /// <summary>git init + one commit. Returns false when git is unavailable.</summary>
    public static bool GitInitWithCommit(string dir, string fileName = "README.md", string content = "# demo\n")
    {
        if (!HasGit)
        {
            return false;
        }

        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, fileName), content);
        return Run("git", dir, "init", "-q", "-b", "main") == 0 &&
               Run("git", dir, "add", "-A") == 0 &&
               Run("git", dir, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "initial commit") == 0;
    }

    /// <summary>Creates a directory junction (no admin rights needed). Returns false if not possible.</summary>
    public static bool TryCreateJunction(string link, string target)
    {
        var cmd = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        return Run(cmd, null, "/d", "/c", "mklink", "/J", link, target) == 0 && Directory.Exists(link);
    }
}
