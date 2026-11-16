using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tezgah.Core.Abstractions;
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
internal sealed partial class CatalogJsonContext : JsonSerializerContext;

/// <summary>
/// Persists <see cref="CatalogState"/> as JSON (default <c>%LOCALAPPDATA%\Tezgah\catalog.json</c>).
/// Writes are atomic (temp file in the same folder, then replace) and keep one <c>.bak</c>; a corrupt file is renamed to
/// <c>.corrupt-&lt;timestamp&gt;</c> and the backup (or an empty state) is used instead. Never throws for bad files.
/// </summary>
internal sealed class CatalogStore : ICatalogStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<CatalogStore> _logger;

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

    public void Dispose() => _gate.Dispose();

    public async Task<CatalogState> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
            {
                // A crash between "move to .bak" and "move temp in" can leave only the backup.
                return await TryReadAsync(BackupPath, ct).ConfigureAwait(false) ?? new CatalogState();
            }

            var state = await TryReadAsync(FilePath, ct).ConfigureAwait(false);
            if (state is not null)
            {
                return state;
            }

            var corrupt = $"{FilePath}.corrupt-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
            try
            {
                File.Move(FilePath, corrupt, overwrite: true);
                _logger.LogWarning("Catalog file was corrupt; moved to {Path}", corrupt);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not move corrupt catalog file");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Could not move corrupt catalog file");
            }

            return await TryReadAsync(BackupPath, ct).ConfigureAwait(false) ?? new CatalogState();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CatalogState?> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                var state = await JsonSerializer.DeserializeAsync(stream, CatalogJsonContext.Default.CatalogState, ct).ConfigureAwait(false);
                return state is null ? null : Normalize(state);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Catalog file {Path} is not valid JSON: {Error}", path, ex.Message);
            return null;
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning("Catalog file {Path} could not be read: {Error}", path, ex.Message);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Catalog file {Path} could not be read: {Error}", path, ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("Catalog file {Path} could not be read: {Error}", path, ex.Message);
            return null;
        }
    }

    /// <summary>Repairs nulls that hand-edited or older files may contain.</summary>
    private static CatalogState Normalize(CatalogState s) => s with
    {
        Items = (s.Items ?? []).Where(i => i is not null && !string.IsNullOrEmpty(i.Id)).ToList(),
        Meta = s.Meta ?? new Dictionary<string, UserMeta>(),
        Settings = s.Settings ?? new TezgahSettings(),
    };

    public async Task SaveAsync(CatalogState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var temp = Path.Combine(dir, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await using (stream.ConfigureAwait(false))
                {
                    await JsonSerializer.SerializeAsync(stream, state, CatalogJsonContext.Default.CatalogState, CancellationToken.None).ConfigureAwait(false);
                    await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(FilePath))
                {
                    try
                    {
                        File.Replace(temp, FilePath, BackupPath, ignoreMetadataErrors: true);
                    }
                    catch (IOException)
                    {
                        // File.Replace can fail on some file systems; fall back to copy-backup + move.
                        File.Copy(FilePath, BackupPath, overwrite: true);
                        File.Move(temp, FilePath, overwrite: true);
                    }
                }
                else
                {
                    File.Move(temp, FilePath);
                }
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
