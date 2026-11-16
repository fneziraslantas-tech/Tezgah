using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tezgah.App.Views;

/// <summary>
/// The Tezgah logo: a rounded blue tile with a white workbench (bench top, two legs) and two work pieces on it.
/// Single source for the in-app logo (<see cref="LogoImage"/>), the application icon (<c>Assets/tezgah.ico</c>) and
/// <c>docs/logo.svg</c>.
/// <para>
/// The shapes are defined on a 48-unit grid. For a target size of N device pixels every edge is rounded to a whole
/// pixel (mirrored shapes are mirrored after rounding so they stay symmetric), so the glyph has hard edges at every size
/// and DPI instead of the half-covered, grey-fringed edges a scaled bitmap or an unsnapped vector produces.
/// </para>
/// </summary>
internal static class LogoArt
{
    private const double Grid = 48;

    /// <summary>Sizes written to the .ico (largest first). Windows picks 16/20/24/32 for 100/125/150/200 % title bars.</summary>
    internal static readonly int[] IconSizes = [256, 128, 64, 48, 40, 32, 24, 20, 16];

    internal static readonly Color TileTop = Color.FromRgb(0x23, 0x8B, 0xEB);
    internal static readonly Color TileBottom = Color.FromRgb(0x0B, 0x4F, 0xB3);

    private const double TileRadius = 10.5;
    private const double GlyphRadius = 1.5;

    // Glyph rectangles on the 48 grid: (x0, y0, x1, y1). Legs are drawn left and mirrored.
    private static readonly Rect PieceLarge = new(new Point(12, 10), new Point(24, 20));
    private static readonly Rect PieceSmall = new(new Point(27, 14), new Point(36, 20));
    private static readonly Rect BenchTop = new(new Point(8, 23), new Point(40, 29));
    private static readonly Rect LegLeft = new(new Point(11, 26), new Point(17, 38));

    private static readonly Dictionary<int, Drawing> Cache = [];

    /// <summary>The logo as a frozen drawing covering (0,0)-(px,px), pixel-snapped for exactly <paramref name="px"/> pixels.</summary>
    internal static Drawing Create(int px)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(px, 8);
        lock (Cache)
        {
            if (Cache.TryGetValue(px, out var cached))
            {
                return cached;
            }

            var drawing = Build(px);
            Cache[px] = drawing;
            return drawing;
        }
    }

    private static DrawingGroup Build(int px)
    {
        var k = px / Grid;
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var tile = new LinearGradientBrush(TileTop, TileBottom, new Point(0, 0), new Point(1, 1));
            tile.Freeze();
            var r = Math.Max(1.5, TileRadius * k);
            dc.DrawRoundedRectangle(tile, null, new Rect(0, 0, px, px), r, r);

            var glyphRadius = GlyphRadius * k >= 0.75 ? GlyphRadius * k : 0;
            var white = Brushes.White;
            foreach (var rect in GlyphRects(px))
            {
                dc.DrawRoundedRectangle(white, null, rect, glyphRadius, glyphRadius);
            }
        }

        group.Freeze();
        return group;
    }

    /// <summary>The white shapes for a <paramref name="px"/>-pixel logo, every edge on a whole pixel.</summary>
    internal static IReadOnlyList<Rect> GlyphRects(int px)
    {
        var k = px / Grid;
        var bench = Snap(BenchTop, k);
        var benchLeft = (int)Math.Round(BenchTop.Left * k, MidpointRounding.AwayFromZero);
        bench = new Rect(new Point(benchLeft, bench.Top), new Point(px - benchLeft, bench.Bottom));
        var leg = Snap(LegLeft, k);
        var legRight = new Rect(new Point(px - leg.Right, leg.Top), new Point(px - leg.Left, leg.Bottom));
        return [Snap(PieceLarge, k), Snap(PieceSmall, k), leg, legRight, bench];
    }

    private static Rect Snap(Rect r, double k)
    {
        var x0 = Math.Round(r.Left * k, MidpointRounding.AwayFromZero);
        var y0 = Math.Round(r.Top * k, MidpointRounding.AwayFromZero);
        var x1 = Math.Max(x0 + 1, Math.Round(r.Right * k, MidpointRounding.AwayFromZero));
        var y1 = Math.Max(y0 + 1, Math.Round(r.Bottom * k, MidpointRounding.AwayFromZero));
        return new Rect(new Point(x0, y0), new Point(x1, y1));
    }

    /// <summary>Renders the logo to a <paramref name="px"/> x <paramref name="px"/> premultiplied bitmap.</summary>
    internal static BitmapSource Render(int px)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawDrawing(Create(px));
        }

        var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Writes a Windows .ico with every size in <see cref="IconSizes"/> rendered separately: 256 px as PNG, the others as
    /// 32-bit DIBs (straight alpha), which every consumer of .ico files understands.
    /// </summary>
    internal static void WriteIco(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var images = IconSizes.Select(s => s >= 64 ? Png(Render(s)) : Dib(Render(s))).ToList();
        using var w = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)IconSizes.Length);
        var offset = 6 + (16 * IconSizes.Length);
        for (var i = 0; i < IconSizes.Length; i++)
        {
            var dim = IconSizes[i] >= 256 ? (byte)0 : (byte)IconSizes[i];
            w.Write(dim);
            w.Write(dim);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }

        foreach (var image in images)
        {
            w.Write(image);
        }
    }

    internal static byte[] Png(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>BITMAPINFOHEADER + bottom-up BGRA rows + an all-zero AND mask (transparency comes from alpha).</summary>
    private static byte[] Dib(BitmapSource bmp)
    {
        var straight = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        var size = straight.PixelWidth;
        var stride = size * 4;
        var pixels = new byte[stride * size];
        straight.CopyPixels(pixels, stride, 0);
        var maskStride = ((size + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(40);
            w.Write(size);
            w.Write(size * 2);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(0);
            w.Write(stride * size);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            for (var y = size - 1; y >= 0; y--)
            {
                w.Write(pixels, y * stride, stride);
            }

            w.Write(new byte[maskStride * size]);
        }

        return ms.ToArray();
    }

    /// <summary>The unsnapped vector (48 x 48 viewBox) as SVG, for docs and the web.</summary>
    internal static string ToSvg()
    {
        static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        var legRight = new Rect(new Point(Grid - LegLeft.Right, LegLeft.Top), new Point(Grid - LegLeft.Left, LegLeft.Bottom));
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"256\" height=\"256\" viewBox=\"0 0 48 48\" role=\"img\" aria-label=\"Tezgah\">\n");
        sb.Append("  <title>Tezgah</title>\n");
        sb.Append(CultureInfo.InvariantCulture, $"  <defs>\n    <linearGradient id=\"tile\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\">\n      <stop offset=\"0\" stop-color=\"{Hex(TileTop)}\"/>\n      <stop offset=\"1\" stop-color=\"{Hex(TileBottom)}\"/>\n    </linearGradient>\n  </defs>\n");
        sb.Append(CultureInfo.InvariantCulture, $"  <rect width=\"48\" height=\"48\" rx=\"{N(TileRadius)}\" fill=\"url(#tile)\"/>\n");
        sb.Append("  <g fill=\"#FFFFFF\">\n");
        foreach (var r in new[] { PieceLarge, PieceSmall, LegLeft, legRight, BenchTop })
        {
            sb.Append(CultureInfo.InvariantCulture, $"    <rect x=\"{N(r.X)}\" y=\"{N(r.Y)}\" width=\"{N(r.Width)}\" height=\"{N(r.Height)}\" rx=\"{N(GlyphRadius)}\"/>\n");
        }

        sb.Append("  </g>\n</svg>\n");
        return sb.ToString();
    }
}

/// <summary>
/// Shows the logo crisply at any size and DPI: renders <see cref="LogoArt"/> snapped to the element's size in device
/// pixels (re-rendered when the DPI changes). Size it with Width/Height; defaults to 24 x 24.
/// </summary>
public sealed class LogoImage : FrameworkElement
{
    public LogoImage()
    {
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Focusable = false;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(24, availableSize.Width), Math.Min(24, availableSize.Height));

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var dip = Math.Min(ActualWidth, ActualHeight);
        if (dip <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var px = Math.Max(8, (int)Math.Round(dip * dpi.DpiScaleX, MidpointRounding.AwayFromZero));
        drawingContext.PushTransform(new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY));
        drawingContext.DrawDrawing(LogoArt.Create(px));
        drawingContext.Pop();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }
}
