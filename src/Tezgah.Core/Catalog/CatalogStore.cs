using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Discovery;
using Tezgah.Core.Model;

namespace Tezgah.Core.Catalog;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(CatalogState))]
[JsonSerializable(typeof(CatalogItem))]
[JsonSerializable(typeof(UserMeta))]
[JsonSerializable(typeof(TezgahSettings))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;

/// <summary>The catalog file exists but could not be read (locked by another program); nothing was loaded or changed.</summary>
public sealed class CatalogUnavailableException : IOException
{
    public CatalogUnavailableException()
    {
    }

    public CatalogUnavailableException(string message)
        : base(message)
    {
    }

    public CatalogUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Persists <see cref="CatalogState"/> as JSON (default <c>%LOCALAPPDATA%\Tezgah\catalog.json</c>).
/// <list type="bullet">
/// <item>Writes are atomic (temp file in the same folder, then replace) and keep one <c>.bak</c>. If the final rename fails
/// the temp file is kept (and picked up by the next load) instead of being deleted.</item>
/// <item>Only real corruption (invalid JSON) moves the file aside to <c>.corrupt-&lt;timestamp&gt;</c>. A file that is merely
/// locked (antivirus, backup, indexer) is retried and, if still unreadable, <see cref="LoadAsync"/> throws
/// <see cref="CatalogUnavailableException"/>: it never pretends the catalog is empty, so nothing overwrites it.</item>
/// <item>Items and metadata entries are read one by one: an unknown enum value or a broken entry (written by a newer
/// Tezgah or edited by hand) drops only that entry. A file with a newer <see cref="CurrentSchemaVersion"/> is loaded
/// read-only: saves are skipped so the newer data is never overwritten.</item>
/// <item>Loaded data is repaired: null collections/entries, out-of-range settings, credentials in remote URLs.</item>
/// </list>
/// </summary>
internal sealed class CatalogStore : ICatalogStore, IDisposable
{
    public const int CurrentSchemaVersion = 1;
    public const int MinForgottenDays = 1;
    public const int MaxForgottenDays = 3650;
    public const int MinScanDepth = 1;
    public const int MaxScanDepth = 32;

    private static readonly int[] RetryDelaysMs = [50, 100, 200, 400, 800];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<CatalogStore> _logger;
    private volatile bool _readOnly;

    public CatalogStore(ILogger<CatalogStore> logger)
        : this(DefaultPath, logger)
    {
    }

    public CatalogStore(string path, ILogger<CatalogStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FilePath = Path.GetFullPath(path);
        _logger = logger ?? NullLogger<CatalogStore>.Instance;
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tezgah", "catalog.json");

    public string FilePath { get; }

    public string BackupPath => FilePath + ".bak";

    /// <summary>True when the loaded file was written by a newer Tezgah: saves are skipped.</summary>
    public bool IsReadOnly => _readOnly;

    public void Dispose() => _gate.Dispose();

    private enum ReadOutcome
    {
        Ok,
        Missing,
        Corrupt,
        Unreadable,
    }

    private sealed record ReadResult(ReadOutcome Outcome, CatalogState? State, bool Newer = false, bool Partial = false, Exception? Error = null);

    public async Task<CatalogState> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var main = await ReadWithRetryAsync(FilePath, ct).ConfigureAwait(false);
            switch (main.Outcome)
            {
                case ReadOutcome.Ok:
                    return Accept(main, FilePath);

                case ReadOutcome.Unreadable:
                    // Locked, not corrupt: never move it aside and never fall back to something that a save would write over it.
                    throw new CatalogUnavailableException(
                        L.T($"Katalog dosyası okunamadı (başka bir program kilitliyor olabilir): {FilePath}", $"The catalog file could not be read (another program may be locking it): {FilePath}"),
                        main.Error!);

                case ReadOutcome.Corrupt:
                    MoveAside(FilePath, "corrupt");
                    break;

                case ReadOutcome.Missing:
                    // A crash between "write temp" and "move into place" can leave the newest state in a temp file.
                    if (await TryNewestTempAsync(ct).ConfigureAwait(false) is { } fromTemp)
                    {
                        return fromTemp;
                    }

                    break;
            }

            var backup = await ReadWithRetryAsync(BackupPath, ct).ConfigureAwait(false);
            if (backup.Outcome == ReadOutcome.Ok)
            {
                return Accept(backup, BackupPath);
            }

            if (backup.Outcome is ReadOutcome.Corrupt or ReadOutcome.Unreadable)
            {
                // Keep it out of File.Replace's single .bak slot so the next save cannot rotate it away.
                MoveAside(BackupPath, "corrupt");
            }

            return new CatalogState();
        }
        finally
        {
            _gate.Release();
        }
    }

    private CatalogState Accept(ReadResult result, string path)
    {
        if (result.Newer)
        {
            _readOnly = true;
            _logger.LogWarning("Catalog {Path} was written by a newer Tezgah (schema > {Version}); loaded read-only, changes will not be saved", path, CurrentSchemaVersion);
        }
        else if (result.Partial)
        {
            // Some entries were dropped (unknown values): keep the full original before it is ever overwritten.
            CopyAside(path, "partial");
        }

        return result.State!;
    }

    private async Task<ReadResult> ReadWithRetryAsync(string path, CancellationToken ct)
    {
        var result = await TryReadAsync(path, ct).ConfigureAwait(false);
        foreach (var delay in RetryDelaysMs)
        {
            if (result.Outcome != ReadOutcome.Unreadable)
            {
                break;
            }

            await Task.Delay(delay, ct).ConfigureAwait(false);
            result = await TryReadAsync(path, ct).ConfigureAwait(false);
        }

        if (result.Outcome == ReadOutcome.Unreadable)
        {
            _logger.LogWarning("Catalog file {Path} could not be read: {Error}", path, result.Error?.Message);
        }

        return result;
    }

    private async Task<ReadResult> TryReadAsync(string path, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            if (!File.Exists(path))
            {
                return new ReadResult(ReadOutcome.Missing, null);
            }

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                doc = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }, ct).ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Catalog file {Path} is not valid JSON: {Error}", path, ex.Message);
            return new ReadResult(ReadOutcome.Corrupt, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ReadResult(ReadOutcome.Unreadable, null, Error: ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ReadResult(ReadOutcome.Corrupt, null);
            }

            var (state, newer, partial) = ParseLeniently(doc.RootElement);
            return new ReadResult(ReadOutcome.Ok, Normalize(state), newer, partial);
        }
    }

    /// <summary>Builds the state part by part so that one unreadable item or meta entry never discards the rest.</summary>
    internal (CatalogState State, bool Newer, bool Partial) ParseLeniently(JsonElement root)
    {
        var partial = false;
        var context = CatalogJsonContext.Default;
        T? Read<T>(JsonElement e, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
            where T : class
        {
            try
            {
                return e.Deserialize(info);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                partial = true;
                _logger.LogWarning("Skipped an unreadable catalog entry: {Error}", ex.Message);
                return null;
            }
        }

        var version = root.TryGetProperty("schemaVersion", out var v) && v.TryGetInt32(out var n) ? n : CurrentSchemaVersion;
        var items = new List<CatalogItem>();
        if (root.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in itemsElement.EnumerateArray())
            {
                if (Read(e, context.CatalogItem) is { } item)
                {
                    items.Add(item);
                }
            }
        }

        var meta = new Dictionary<string, UserMeta>(StringComparer.Ordinal);
        if (root.TryGetProperty("meta", out var metaElement) && metaElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in metaElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Null && Read(p.Value, context.UserMeta) is { } m)
                {
                    meta[p.Name] = m;
                }
            }
        }

        TezgahSettings? settings = null;
        if (root.TryGetProperty("settings", out var settingsElement) && settingsElement.ValueKind == JsonValueKind.Object)
        {
            settings = Read(settingsElement, context.TezgahSettings);
        }

        DateTimeOffset? lastScan = root.TryGetProperty("lastScanAt", out var ls) && ls.ValueKind == JsonValueKind.String && ls.TryGetDateTimeOffset(out var at) ? at : null;
        var state = new CatalogState
        {
            SchemaVersion = Math.Min(version, CurrentSchemaVersion),
            LastScanAt = lastScan,
            Items = items,
            Meta = meta,
            Settings = settings ?? new TezgahSettings(),
        };
        return (state, version > CurrentSchemaVersion, partial);
    }

    /// <summary>Repairs nulls and out-of-range values that hand-edited, partially written or older files may contain.</summary>
    internal static CatalogState Normalize(CatalogState s)
    {
        var defaults = new TezgahSettings();
        var settings = s.Settings ?? defaults;
        settings = settings with
        {
            ScanRoots = (settings.ScanRoots ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).ToList(),
            ExcludedDirectoryNames = settings.ExcludedDirectoryNames is null ? defaults.ExcludedDirectoryNames : settings.ExcludedDirectoryNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
            ForgottenAfterDays = Math.Clamp(settings.ForgottenAfterDays, MinForgottenDays, MaxForgottenDays),
            MaxDepth = Math.Clamp(settings.MaxDepth, MinScanDepth, MaxScanDepth),
            Language = settings.Language ?? defaults.Language,
            Theme = settings.Theme ?? defaults.Theme,
        };

        var items = (s.Items ?? [])
            .Where(i => i is not null && !string.IsNullOrEmpty(i.Id) && i.Name is not null && i.Source is not null)
            .Select(i => i with
            {
                ProjectTypes = (i.ProjectTypes ?? []).Where(t => t is not null).ToList(),
                RunProfiles = (i.RunProfiles ?? []).Where(p => p is not null && p.Command is not null && p.Id is not null).Select(NormalizeProfile).ToList(),
                Artifacts = (i.Artifacts ?? []).Where(a => a is not null && a.Path is not null).ToList(),
                Git = i.Git is null ? null : i.Git with
                {
                    RemoteUrl = GitUrls.Sanitize(i.Git.RemoteUrl), // files written before credential stripping was complete
                    UnpushedBranches = i.Git.UnpushedBranches ?? [],
                    LinkedWorktrees = i.Git.LinkedWorktrees ?? [],
                },
            })
            .DistinctBy(i => i.Id, StringComparer.Ordinal)
            .ToList();

        var meta = new Dictionary<string, UserMeta>(StringComparer.Ordinal);
        foreach (var (key, value) in s.Meta ?? new Dictionary<string, UserMeta>())
        {
            if (string.IsNullOrEmpty(key) || value is null)
            {
                continue;
            }

            meta[key] = value with
            {
                Tags = (value.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToList(),
                CustomProfiles = (value.CustomProfiles ?? []).Where(p => p is not null && p.Command is not null && p.Id is not null).Select(NormalizeProfile).ToList(),
            };
        }

        return s with { Items = items, Meta = meta, Settings = settings };
    }

    private static RunProfile NormalizeProfile(RunProfile p) => p with
    {
        Label = p.Label ?? p.Id,
        Origin = p.Origin ?? string.Empty,
        Command = p.Command with
        {
            Arguments = (p.Command.Arguments ?? []).Where(a => a is not null).ToList(),
            Environment = p.Command.Environment ?? new Dictionary<string, string>(),
        },
    };

    public async Task SaveAsync(CatalogState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_readOnly)
        {
            _logger.LogWarning("Catalog is read-only (written by a newer Tezgah); not saved");
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var temp = Path.Combine(dir, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
            var written = false;
            var committed = false;
            try
            {
                var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await using (stream.ConfigureAwait(false))
                {
                    await JsonSerializer.SerializeAsync(stream, state, CatalogJsonContext.Default.CatalogState, CancellationToken.None).ConfigureAwait(false);
                    await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                written = true;
                await CommitAsync(temp).ConfigureAwait(false);
                committed = true;
            }
            finally
            {
                // Delete the temp only when it is not the sole copy of the new state.
                if ((committed || !written) && File.Exists(temp))
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (IOException)
                    {
                    }
                }
                else if (written && !committed)
                {
                    _logger.LogError("Catalog could not be moved into place; the new state is kept in {Temp}", temp);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Moves <paramref name="temp"/> over the catalog keeping a backup. File.Replace is retried on sharing violations; its
    /// partial failures are resolved from what is actually on disk: with ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 (1177) the
    /// original was already renamed to the backup and the catalog path no longer exists.
    /// </summary>
    internal async Task CommitAsync(string temp)
    {
        if (!File.Exists(FilePath))
        {
            File.Move(temp, FilePath);
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Replace(temp, FilePath, BackupPath, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException) when (attempt < 3 && File.Exists(FilePath))
            {
                await Task.Delay(75 * attempt).ConfigureAwait(false);
            }
            catch (IOException)
            {
                break;
            }
        }

        CommitFallback(temp);
    }

    /// <summary>Copy-backup + move, based on the state on disk (never copies a file that is not there).</summary>
    internal void CommitFallback(string temp)
    {
        if (File.Exists(FilePath))
        {
            File.Copy(FilePath, BackupPath, overwrite: true);
        }

        File.Move(temp, FilePath, overwrite: true);
    }

    private async Task<CatalogState?> TryNewestTempAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        if (!Directory.Exists(dir))
        {
            return null;
        }

        foreach (var tmp in new DirectoryInfo(dir).EnumerateFiles($".{Path.GetFileName(FilePath)}.*.tmp").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            var r = await TryReadAsync(tmp.FullName, ct).ConfigureAwait(false);
            if (r.Outcome == ReadOutcome.Ok && !r.Newer)
            {
                _logger.LogWarning("Recovered the catalog from an uncommitted save: {Temp}", tmp.FullName);
                return r.State;
            }
        }

        return null;
    }

    private void MoveAside(string path, string tag)
    {
        var target = $"{path}.{tag}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        try
        {
            File.Move(path, target, overwrite: true);
            _logger.LogWarning("Catalog file {Path} moved to {Target}", path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not move {Path} aside", path);
        }
    }

    private void CopyAside(string path, string tag)
    {
        var target = $"{path}.{tag}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        try
        {
            File.Copy(path, target, overwrite: true);
            _logger.LogWarning("Some catalog entries could not be read; the original was kept as {Target}", target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not copy {Path} aside", path);
        }
    }
}
