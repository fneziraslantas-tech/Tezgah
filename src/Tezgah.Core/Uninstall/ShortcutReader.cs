using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Tezgah.Core.Uninstall;

/// <summary>
/// Minimal, read-only parser for Windows shell links (.lnk, [MS-SHLLINK]). Extracts the target path without COM,
/// WScript or resolving the link (resolving can touch the network or "repair" the link). Malformed input yields null.
/// </summary>
internal static class ShortcutReader
{
    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIdList = 0x1;
    private const uint HasLinkInfo = 0x2;
    private const uint HasName = 0x4;
    private const uint HasRelativePath = 0x8;
    private const uint HasWorkingDir = 0x10;
    private const uint HasArguments = 0x20;
    private const uint HasIconLocation = 0x40;
    private const uint IsUnicode = 0x80;
    private const uint EnvironmentVariableBlock = 0xA0000001;
    private const long MaxLinkFileBytes = 1024 * 1024;

    /// <summary>Returns the shortcut's target path (environment variables expanded), or null.</summary>
    public static string? TryGetTarget(string lnkPath)
    {
        try
        {
            var info = new FileInfo(lnkPath);
            if (!info.Exists || info.Length < HeaderSize || info.Length > MaxLinkFileBytes)
            {
                return null;
            }

            return Parse(File.ReadAllBytes(lnkPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            return ParseCore(data);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? ParseCore(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != HeaderSize)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]);
        var pos = HeaderSize;
        if ((flags & HasLinkTargetIdList) != 0)
        {
            pos += 2 + BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        }

        string? target = null;
        if ((flags & HasLinkInfo) != 0)
        {
            var linkInfo = data[pos..];
            var linkInfoSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(linkInfo);
            linkInfo = linkInfo[..linkInfoSize];
            target = ReadLinkInfo(linkInfo);
            pos += linkInfoSize;
        }

        var unicode = (flags & IsUnicode) != 0;
        foreach (var flag in new[] { HasName, HasRelativePath, HasWorkingDir, HasArguments, HasIconLocation })
        {
            if ((flags & flag) != 0)
            {
                var count = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                pos += 2 + (unicode ? count * 2 : count);
            }
        }

        // Extra data: EnvironmentVariableDataBlock holds targets like "%ProgramFiles%\App\app.exe".
        while (target is null && pos + 8 <= data.Length)
        {
            var blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
            if (blockSize < 8 || pos + blockSize > data.Length)
            {
                break;
            }

            var signature = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            if (signature == EnvironmentVariableBlock && blockSize >= 0x314)
            {
                var unicodeTarget = ReadUtf16Z(data.Slice(pos + 8 + 260, 520));
                target = Environment.ExpandEnvironmentVariables(unicodeTarget.Length > 0 ? unicodeTarget : ReadAnsiZ(data.Slice(pos + 8, 260)));
            }

            pos += blockSize;
        }

        return string.IsNullOrWhiteSpace(target) ? null : target;
    }

    private static string? ReadLinkInfo(ReadOnlySpan<byte> linkInfo)
    {
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[4..]);
        var linkInfoFlags = BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[8..]);
        if ((linkInfoFlags & 0x1) == 0)
        {
            return null; // not a local target (network-only link)
        }

        var localBaseOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[16..]);
        var suffixOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[24..]);
        if (headerSize >= 0x24)
        {
            var localBaseUnicode = (int)BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[28..]);
            var suffixUnicode = (int)BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[32..]);
            if (localBaseUnicode > 0)
            {
                var basePath = ReadUtf16Z(linkInfo[localBaseUnicode..]);
                var suffix = suffixUnicode > 0 ? ReadUtf16Z(linkInfo[suffixUnicode..]) : string.Empty;
                return Join(basePath, suffix);
            }
        }

        var ansiBase = ReadAnsiZ(linkInfo[localBaseOffset..]);
        var ansiSuffix = suffixOffset > 0 ? ReadAnsiZ(linkInfo[suffixOffset..]) : string.Empty;
        return Join(ansiBase, ansiSuffix);
    }

    private static string Join(string basePath, string suffix) =>
        suffix.Length == 0 ? basePath : basePath.EndsWith('\\') ? basePath + suffix : basePath + "\\" + suffix;

    private static string ReadUtf16Z(ReadOnlySpan<byte> span)
    {
        var length = 0;
        while (length + 1 < span.Length && (span[length] != 0 || span[length + 1] != 0))
        {
            length += 2;
        }

        return Encoding.Unicode.GetString(span[..length]);
    }

    private static string ReadAnsiZ(ReadOnlySpan<byte> span)
    {
        var end = span.IndexOf((byte)0);
        var bytes = end >= 0 ? span[..end] : span;
        Encoding encoding;
        try
        {
            encoding = CodePagesEncodingProvider.Instance.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage) ?? Encoding.Latin1;
        }
        catch (ArgumentException)
        {
            encoding = Encoding.Latin1;
        }

        return encoding.GetString(bytes);
    }
}
