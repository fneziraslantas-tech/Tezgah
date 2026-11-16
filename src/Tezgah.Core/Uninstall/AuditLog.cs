using System.Text.Json;
using System.Text.Json.Serialization;
using Tezgah.Core.Model;

namespace Tezgah.Core.Uninstall;

/// <summary>Append-only JSON-lines record of every destructive action Tezgah performed.</summary>
internal sealed class AuditLog(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public string Path { get; } = path;

    public async Task WriteAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // Share with readers/other instances; retry briefly on sharing violations (AV, backup, second Tezgah).
                    await using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(100 * attempt, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Like <see cref="WriteAsync"/> but never throws (except cancellation): returns the error message or null.</summary>
    public async Task<string?> TryWriteAsync(AuditEntry entry, CancellationToken ct)
    {
        try
        {
            await WriteAsync(entry, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
            return ex.Message;
        }
    }
}

internal sealed record AuditEntry(
    DateTimeOffset Timestamp,
    string Action,
    string ItemId,
    string ItemName,
    string Outcome,
    IReadOnlyList<AuditStep> Steps,
    string? Error = null,
    string? RunId = null);

internal sealed record AuditStep(UninstallStepKind Kind, string? Path, string? Command, bool Success, string? Error);
