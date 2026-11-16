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
        var line = JsonSerializer.Serialize(entry, JsonOptions) + "\n";
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
            }

            await File.AppendAllTextAsync(Path, line, ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
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
    string? Error = null);

internal sealed record AuditStep(UninstallStepKind Kind, string? Path, string? Command, bool Success, string? Error);
