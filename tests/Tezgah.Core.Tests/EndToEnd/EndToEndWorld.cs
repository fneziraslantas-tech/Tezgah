using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Catalog;
using Tezgah.Core.Execution;
using Tezgah.Core.Safety;
using Tezgah.Core.Model;
using Tezgah.Core.Uninstall;

namespace Tezgah.Core.Tests.EndToEnd;

/// <summary>
/// A throw-away "developer PC" under %TEMP%\tezgah-e2e\&lt;id&gt;: a scan root with fake projects, plus a private state
/// folder holding the catalog file and audit log. Services come from the real composition root
/// (<c>AddLogging()</c> + <c>AddTezgahCore()</c>); only the two storage locations are redirected so the user's real
/// %LOCALAPPDATA%\Tezgah is never read or written.
/// </summary>
internal sealed class EndToEndWorld : IAsyncDisposable
{
    private readonly List<ServiceProvider> _providers = [];

    public EndToEndWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "tezgah-e2e", Guid.NewGuid().ToString("N")[..12]);
        ScanRoot = Path.Combine(Root, "scan");
        StateDir = Path.Combine(Root, "state");
        Directory.CreateDirectory(ScanRoot);
        Directory.CreateDirectory(StateDir);
    }

    public string Root { get; }

    public string ScanRoot { get; }

    public string StateDir { get; }

    public string CatalogPath => Path.Combine(StateDir, "catalog.json");

    public string AuditLogPath => Path.Combine(StateDir, "audit.log");

    public string GitRepoPath => Path.Combine(ScanRoot, "demo-server");

    public string NodePath => Path.Combine(ScanRoot, "web-app");

    public string PlainPath => Path.Combine(ScanRoot, "just-notes");

    /// <summary>True when the git repo runs <c>server.py</c> with Python; false when it falls back to <c>run.cmd</c>.</summary>
    public bool UsesPython { get; private set; }

    public int Port { get; private set; }

    public static bool HasGit => Run("git", null, "--version") == 0;

    /// <summary>Real Python (not the Microsoft Store stub, which exits non-zero when Python is not installed).</summary>
    public static bool HasPython => Run("python", null, "-c", "import sys; sys.exit(0)") == 0;

    /// <summary>Builds the real core over this world's private catalog file and audit log.</summary>
    public ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTezgahCore();
        services.Replace(ServiceDescriptor.Singleton<ICatalogStore>(sp => new CatalogStore(CatalogPath, sp.GetRequiredService<ILogger<CatalogStore>>())));
        services.Replace(ServiceDescriptor.Singleton(new UninstallOptions
        {
            AuditLogPath = AuditLogPath,
            VirtualenvRoots = [],
            ShortcutRootsRecursive = [],
            ShortcutRootsTopLevel = [],
            CommandTimeout = TimeSpan.FromMinutes(1),
        }));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        _providers.Add(provider);
        return provider;
    }

    /// <summary>
    /// (a) a git repository with a dev-server script that prints a localhost URL, spawns a child process and sleeps,
    /// (b) a node project (package.json only, no install) with a fake node_modules,
    /// (c) a plain folder that is not a project.
    /// </summary>
    public void CreateProjects()
    {
        Port = FreePort();
        Directory.CreateDirectory(GitRepoPath);
        UsesPython = HasPython;
        if (UsesPython)
        {
            File.WriteAllText(Path.Combine(GitRepoPath, "server.py"), $$"""
                import subprocess, sys, time
                child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(600)"])
                print("child-pid=%d" % child.pid, flush=True)
                print("Serving on http://127.0.0.1:{{Port}}/", flush=True)
                time.sleep(600)
                """);
        }
        else
        {
            // ping as the long-running child keeps the cmd.exe tree alive like a dev server would.
            File.WriteAllText(Path.Combine(GitRepoPath, "run.cmd"), $"""
                @echo off
                echo Serving on http://127.0.0.1:{Port}/
                ping -n 600 127.0.0.1 >nul
                """);
        }

        File.WriteAllText(Path.Combine(GitRepoPath, "README.md"), "# demo server\n");
        Git("init", "-q", "-b", "main");
        Git("add", "-A");
        Git("-c", "user.name=Tezgah E2E", "-c", "user.email=e2e@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "initial commit");

        Directory.CreateDirectory(NodePath);
        File.WriteAllText(Path.Combine(NodePath, "package.json"), """
            {
              "name": "web-app",
              "version": "1.0.0",
              "description": "E2E node project",
              "scripts": { "dev": "node index.js", "build": "echo build" }
            }
            """);
        var nodeModules = Path.Combine(NodePath, "node_modules", "left-pad");
        Directory.CreateDirectory(nodeModules);
        File.WriteAllText(Path.Combine(nodeModules, "package.json"), """{ "name": "left-pad", "version": "1.3.0" }""");
        File.WriteAllBytes(Path.Combine(nodeModules, "index.js"), new byte[64 * 1024]);

        Directory.CreateDirectory(PlainPath);
        File.WriteAllText(Path.Combine(PlainPath, "notes.txt"), "not a project\n");
    }

    public async Task ConfigureAndScanAsync(ICatalogService catalog)
    {
        await catalog.UpdateSettingsAsync(
            s => s with { ScanRoots = [ScanRoot], IncludeInstalledApps = false, IncludePackageTools = false },
            CancellationToken.None);
        await catalog.ScanAsync(null, CancellationToken.None);
    }

    public static CatalogItem Item(ICatalogService catalog, string path) =>
        catalog.State.Items.Single(i => i.Id == ItemIds.ForPath(path));

    public void Git(params string[] args)
    {
        var code = Run("git", GitRepoPath, args);
        if (code != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed with {code}");
        }
    }

    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    public static bool IsProcessGone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            if (provider.GetService<IRunService>() is { } runs)
            {
                await runs.StopAllAsync(CancellationToken.None);
            }

            await provider.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(Root))
            {
                FileRemover.DeleteTreePermanently(Root, isRoot: true, CancellationToken.None);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static int Run(string file, string? workingDirectory, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000))
            {
                p.Kill(true);
                return -1;
            }

            Task.WaitAll(stdout, stderr);
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }
}
