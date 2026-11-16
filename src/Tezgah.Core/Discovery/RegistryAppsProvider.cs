using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.Core.Discovery;

/// <summary>
/// Programs from "Apps &amp; features": HKLM (64-bit and WOW6432Node) and HKCU <c>...\CurrentVersion\Uninstall</c>. Read only.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class RegistryAppsProvider(ILogger<RegistryAppsProvider> logger) : IDiscoveryProvider
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public string Id => Sources.Registry;

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(OperatingSystem.IsWindows());

    public Task<IReadOnlyList<CatalogItem>> DiscoverAsync(TezgahSettings settings, IProgress<ScanProgress>? progress, CancellationToken ct) =>
        Task.Run<IReadOnlyList<CatalogItem>>(() =>
        {
            var items = new List<CatalogItem>();
            var hives = new (RegistryHive Hive, RegistryView View, string Tag)[]
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64, "hklm64"),
                (RegistryHive.LocalMachine, RegistryView.Registry32, "hklm32"),
                (RegistryHive.CurrentUser, RegistryView.Default, "hkcu"),
            };
            foreach (var (hive, view, tag) in hives)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(UninstallKey);
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            using var key = uninstall.OpenSubKey(name);
                            if (key is null)
                            {
                                continue;
                            }

                            var item = ReadEntry(tag, name, n => key.GetValue(n));
                            if (item is not null)
                            {
                                items.Add(item);
                            }
                        }
                        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                        {
                            logger.LogDebug("Cannot read uninstall key {Key}: {Error}", name, ex.Message);
                        }
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    logger.LogInformation("Cannot open {Hive} uninstall keys: {Error}", tag, ex.Message);
                }
            }

            progress?.Report(new ScanProgress("registry", null, items.Count));

            // On 64-bit Windows the 32/64 views sometimes reflect the same key; keep one per id.
            return items.DistinctBy(i => i.Id).ToList();
        }, ct);

    /// <summary>Maps one Uninstall subkey to a catalog item. <paramref name="get"/> reads a value by name. Exposed for tests.</summary>
    internal static CatalogItem? ReadEntry(string hiveTag, string keyName, Func<string, object?> get)
    {
        string? Str(string n) => get(n) is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        int? Int(string n) => get(n) switch
        {
            int i => i,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) => v,
            _ => null,
        };

        var displayName = Str("DisplayName");
        if (displayName is null || Int("SystemComponent") == 1 || Str("ParentKeyName") is not null ||
            KbUpdate().IsMatch(keyName) || KbUpdate().IsMatch(displayName) || Str("ReleaseType") is "Update" or "Hotfix" or "Security Update")
        {
            return null;
        }

        var uninstallString = Str("QuietUninstallString") ?? Str("UninstallString");
        var installLocation = Str("InstallLocation")?.Trim('"');
        if (installLocation is not null)
        {
            installLocation = Environment.ExpandEnvironmentVariables(installLocation);
            installLocation = Path.IsPathFullyQualified(installLocation) ? Path.TrimEndingDirectorySeparator(installLocation) : null;
        }

        CommandSpec? uninstall = null;
        var parsed = UninstallStringParser.Parse(uninstallString);
        if (parsed is not null)
        {
            var exeDir = Path.IsPathFullyQualified(parsed.FileName) ? Path.GetDirectoryName(parsed.FileName) : null;
            uninstall = new CommandSpec
            {
                FileName = parsed.FileName,
                Arguments = parsed.Arguments,
                WorkingDirectory = exeDir ?? installLocation ?? Environment.GetFolderPath(Environment.SpecialFolder.System),
            };
        }

        DateTimeOffset? installedAt = null;
        var installDate = Str("InstallDate");
        if (installDate is not null &&
            DateTime.TryParseExact(installDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
        {
            installedAt = new DateTimeOffset(d);
        }

        long? size = Int("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null;
        var web = Str("URLInfoAbout") ?? Str("HelpLink") ?? Str("URLUpdateInfo");
        if (web is not null && !(web.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || web.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            web = null;
        }

        return new CatalogItem
        {
            Id = ItemIds.ForSource(Sources.Registry, $"{hiveTag}\\{keyName}"),
            Kind = ItemKind.InstalledApp,
            Source = Sources.Registry,
            Name = displayName,
            Path = installLocation,
            Version = Str("DisplayVersion"),
            Publisher = Str("Publisher"),
            Description = Str("Comments"),
            InstalledAt = installedAt,
            LastActivity = installedAt,
            SizeBytes = size,
            UninstallCommand = uninstall,
            WebUrl = web,
        };
    }

    [GeneratedRegex(@"^KB\d{5,}|\(KB\d{5,}\)|Update for .*KB\d{5,}", RegexOptions.CultureInvariant)]
    private static partial Regex KbUpdate();
}
