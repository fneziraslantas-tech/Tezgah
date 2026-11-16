using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tezgah.App.ViewModels;
using Tezgah.App.Views;

namespace Tezgah.App.Tests;

public class AboutAndLogoTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tezgah.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("Tezgah.sln not found");
        }
    }

    [Fact]
    public void F1_and_the_about_button_open_the_about_dialog()
    {
        var h = new Harness();
        h.Vm.ShowAboutCommand.Execute(null);
        var about = Assert.Single(h.Dialogs.AboutShown);
        Assert.False(string.IsNullOrWhiteSpace(about.Version));
        Assert.DoesNotContain("+", about.Version, StringComparison.Ordinal);
        Assert.Contains(about.Shortcuts, s => s.Keys == "F1");
        var xaml = File.ReadAllText(Path.Combine(RepoRoot, "src", "Tezgah.App", "MainWindow.xaml"));
        Assert.Contains("<KeyBinding Key=\"F1\" Command=\"{Binding ShowAboutCommand}\" />", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void About_links_go_to_the_project_and_the_log_folder()
    {
        var shell = new FakeShell();
        var logs = Path.Combine(Path.GetTempPath(), "tezgah-about-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var vm = new AboutViewModel(shell, new KeyStrings(), logs, "1.2.3");
            vm.OpenRepositoryCommand.Execute(null);
            vm.OpenLicenseCommand.Execute(null);
            vm.OpenLogFolderCommand.Execute(null);

            Assert.Equal(["https://github.com/fneziraslantas-tech/Tezgah", "https://github.com/fneziraslantas-tech/Tezgah/blob/main/LICENSE"],
                shell.Opened.Select(u => u.AbsoluteUri.TrimEnd('/')));
            Assert.Equal([logs], shell.OpenedFolders);
            Assert.True(Directory.Exists(logs));
            Assert.Null(vm.Error);
            Assert.Equal("About_Version(1.2.3)", vm.VersionText);
        }
        finally
        {
            Directory.Delete(logs, recursive: true);
        }
    }

    [Fact]
    public void Version_drops_build_metadata()
    {
        Assert.Equal(AboutViewModel.AppVersion(typeof(App).Assembly).Split('+')[0], AboutViewModel.AppVersion(typeof(App).Assembly));
        var informational = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.StartsWith(AboutViewModel.AppVersion(typeof(App).Assembly), informational, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(32)]
    [InlineData(36)]
    [InlineData(48)]
    [InlineData(256)]
    public void Logo_glyph_edges_are_whole_pixels_and_symmetric(int px)
    {
        var rects = LogoArt.GlyphRects(px);
        foreach (var r in rects)
        {
            foreach (var edge in new[] { r.Left, r.Top, r.Right, r.Bottom })
            {
                Assert.Equal(Math.Round(edge), edge);
            }

            Assert.True(r.Width >= 1 && r.Height >= 1);
            Assert.True(r.Left >= 0 && r.Top >= 0 && r.Right <= px && r.Bottom <= px);
        }

        // legs (index 2 and 3) and the bench top (4) are mirror images around the vertical centre line
        Assert.Equal(px - rects[2].Right, rects[3].Left);
        Assert.Equal(px - rects[2].Left, rects[3].Right);
        Assert.Equal(px - rects[4].Right, rects[4].Left);
    }

    [Fact]
    public void Small_logo_renders_have_hard_glyph_edges()
    {
        // Snapped edges: every glyph pixel is either (almost) pure white or (almost) pure tile colour. A downscaled
        // bitmap or an unsnapped vector leaves whole rows of half-blended pixels along each edge.
        foreach (var px in new[] { 16, 24, 32, 48 })
        {
            var pixels = Pixels(LogoArt.Render(px));
            var inner = 0;
            var blended = 0;
            var margin = (int)Math.Ceiling(px * 0.25); // skip the anti-aliased rounded tile corners
            for (var y = 0; y < px; y++)
            {
                for (var x = margin; x < px - margin; x++)
                {
                    var i = ((y * px) + x) * 4;
                    var (b, g, r) = (pixels[i], pixels[i + 1], pixels[i + 2]);
                    inner++;
                    var white = r > 245 && g > 245 && b > 245;
                    var tile = b - r > 120; // the blue gradient
                    if (!white && !tile)
                    {
                        blended++;
                    }
                }
            }

            // only the rounded corners of the 5 glyph shapes may be anti-aliased (none below 24 px, where they are square)
            var allowed = px < 24 ? 0 : 5 * 4 * 3;
            Assert.True(blended <= allowed, $"{px}px: {blended} of {inner} pixels are blended");
        }
    }

    [Fact]
    public void Committed_icon_and_svg_match_the_logo_definition()
    {
        // Assets/make-icon.ps1 regenerates both from LogoArt; this fails when the drawing changed but they were not rebuilt.
        var icoPath = Path.Combine(RepoRoot, "src", "Tezgah.App", "Assets", "tezgah.ico");
        using (var fs = File.OpenRead(icoPath))
        {
            var decoder = new IconBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(LogoArt.IconSizes.OrderBy(s => s), decoder.Frames.Select(f => f.PixelWidth).OrderBy(s => s));
            foreach (var frame in decoder.Frames)
            {
                var expected = Pixels(LogoArt.Render(frame.PixelWidth));
                var actual = Pixels(frame);
                var maxDiff = expected.Zip(actual, (a, b) => Math.Abs(a - b)).Max();
                Assert.True(maxDiff <= 3, $"{frame.PixelWidth}px frame differs from LogoArt by {maxDiff}");
            }
        }

        var svg = File.ReadAllText(Path.Combine(RepoRoot, "docs", "logo.svg")).ReplaceLineEndings("\n");
        Assert.Equal(LogoArt.ToSvg(), svg);
    }

    /// <summary>Straight-alpha BGRA bytes.</summary>
    private static byte[] Pixels(BitmapSource bmp)
    {
        var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
