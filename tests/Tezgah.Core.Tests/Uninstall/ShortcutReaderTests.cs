using System.Text;
using Tezgah.Core.Uninstall;

namespace Tezgah.Core.Tests.Uninstall;

/// <summary>Writes minimal [MS-SHLLINK] files (header + LinkInfo) so tests do not need COM to create shortcuts.</summary>
internal static class LnkBuilder
{
    public static byte[] Build(string target)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x4C);
        w.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
        w.Write(0x2u | 0x80u); // HasLinkInfo | IsUnicode
        w.Write(new byte[0x4C - 0x18]);

        var ansi = Encoding.ASCII.GetBytes(target.Any(c => c > 127) ? "?" : target);
        var unicode = Encoding.Unicode.GetBytes(target);
        const int headerSize = 0x24;
        const int volumeIdSize = 0x11;
        var localBaseOffset = headerSize + volumeIdSize;
        var suffixOffset = localBaseOffset + ansi.Length + 1;
        var unicodeBaseOffset = suffixOffset + 1;
        var unicodeSuffixOffset = unicodeBaseOffset + unicode.Length + 2;
        var total = unicodeSuffixOffset + 2;

        w.Write(total);
        w.Write(headerSize);
        w.Write(1); // VolumeIDAndLocalBasePath
        w.Write(headerSize); // VolumeIDOffset
        w.Write(localBaseOffset);
        w.Write(0); // CommonNetworkRelativeLinkOffset
        w.Write(suffixOffset);
        w.Write(unicodeBaseOffset);
        w.Write(unicodeSuffixOffset);

        w.Write(volumeIdSize);
        w.Write(3); // DRIVE_FIXED
        w.Write(0x1234);
        w.Write(0x10); // label offset
        w.Write((byte)0);

        w.Write(ansi);
        w.Write((byte)0);
        w.Write((byte)0); // empty suffix
        w.Write(unicode);
        w.Write((short)0);
        w.Write((short)0);

        w.Write(0); // terminal extra-data block
        return ms.ToArray();
    }

    public static string Write(string lnkPath, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        File.WriteAllBytes(lnkPath, Build(target));
        return lnkPath;
    }
}

public class ShortcutReaderTests
{
    [Theory]
    [InlineData(@"C:\dev\tool\app.exe")]
    [InlineData(@"C:\Kullanıcılar\Furkan\proje\çalıştır.exe")]
    public void Reads_target_from_link_info(string target) => Assert.Equal(target, ShortcutReader.Parse(LnkBuilder.Build(target)));

    [Fact]
    public void Garbage_is_rejected()
    {
        Assert.Null(ShortcutReader.Parse(new byte[10]));
        Assert.Null(ShortcutReader.Parse(new byte[200]));
        var truncated = LnkBuilder.Build(@"C:\dev\app.exe")[..90];
        Assert.Null(ShortcutReader.Parse(truncated));
    }
}
