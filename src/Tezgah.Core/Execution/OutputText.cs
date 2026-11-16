using System.Text.RegularExpressions;

namespace Tezgah.Core.Execution;

/// <summary>Helpers for console output: ANSI stripping and local dev-server URL detection.</summary>
internal static partial class OutputText
{
    /// <summary>Removes ANSI/VT escape sequences (CSI, OSC, single-char escapes) and other control characters except tab.</summary>
    public static string StripAnsi(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        var stripped = text.Contains('\x1B', StringComparison.Ordinal) || text.Contains('\x9B', StringComparison.Ordinal)
            ? AnsiEscape().Replace(text, string.Empty)
            : text;
        return stripped.Any(c => char.IsControl(c) && c != '\t')
            ? new string(stripped.Where(c => !char.IsControl(c) || c == '\t').ToArray())
            : stripped;
    }

    /// <summary>Finds http(s) URLs pointing at the local machine. 0.0.0.0 and [::] are normalised to localhost.</summary>
    public static IEnumerable<Uri> FindLocalUrls(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (Match match in LocalUrl().Matches(text))
        {
            var value = match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '\'', '"', '>', '!', '?');
            value = value.Replace("://0.0.0.0", "://localhost", StringComparison.OrdinalIgnoreCase)
                         .Replace("://[::]", "://localhost", StringComparison.OrdinalIgnoreCase);
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                yield return uri;
            }
        }
    }

    [GeneratedRegex(@"(?:\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)?|\x1B\[[0-?]*[ -/]*[@-~]|\x9B[0-?]*[ -/]*[@-~]|\x1B[ -/]*[0-~])", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]|\[::\])(?::\d{1,5})?(?![\w-]|\.[\w-])(?:[/?#][^\s<>""'`]*)?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LocalUrl();
}

/// <summary>Thread-safe bounded FIFO; the oldest items are dropped once <see cref="Capacity"/> is reached.</summary>
internal sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private readonly Lock _gate = new();
    private int _start;
    private int _count;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Add(T item)
    {
        lock (_gate)
        {
            if (_count < _items.Length)
            {
                _items[(_start + _count) % _items.Length] = item;
                _count++;
            }
            else
            {
                _items[_start] = item;
                _start = (_start + 1) % _items.Length;
            }
        }
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate)
        {
            var result = new T[_count];
            for (var i = 0; i < _count; i++)
            {
                result[i] = _items[(_start + i) % _items.Length];
            }

            return result;
        }
    }
}
