using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tezgah.App.Demo;
using Tezgah.App.ViewModels;
using Tezgah.Core.Model;

namespace Tezgah.App.Tests;

public partial class FormattingAndResourcesTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData(0L, "0 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
    [InlineData(412L * 1024 * 1024, "412 MB")]
    public void Bytes_formats_with_the_ui_culture(long? bytes, string expected)
    {
        var old = Formatting.Culture;
        Formatting.Culture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, Formatting.Bytes(bytes));
        }
        finally
        {
            Formatting.Culture = old;
        }
    }

    [Fact]
    public void Bytes_uses_turkish_decimal_comma()
    {
        var old = Formatting.Culture;
        Formatting.Culture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            Assert.Equal("20,4 GB", Formatting.Bytes((long)(20.4 * 1024 * 1024 * 1024)));
        }
        finally
        {
            Formatting.Culture = old;
        }
    }

    [Theory]
    [InlineData("git@github.com:furkan/whisper.git", "https://github.com/furkan/whisper")]
    [InlineData("https://github.com/a/b.git", "https://github.com/a/b")]
    [InlineData("https://token:x-oauth@github.com/a/b.git", "https://github.com/a/b")]
    [InlineData("ssh://git@gitlab.com/a/b.git", "https://gitlab.com/a/b")]
    [InlineData("C:\\local\\repo", null)]
    [InlineData(null, null)]
    public void Remote_to_web_url(string? remote, string? expected) =>
        Assert.Equal(expected, Formatting.RemoteToWeb(remote)?.AbsoluteUri);

    [Fact]
    public void Relative_time_buckets()
    {
        var s = new KeyStrings();
        var now = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("Time_JustNow", Formatting.Relative(now.AddSeconds(-5), now, s));
        Assert.Equal("Time_Yesterday", Formatting.Relative(now.AddHours(-30), now, s));
        Assert.Equal("Time_DaysAgo(5)", Formatting.Relative(now.AddDays(-5), now, s));
        Assert.Equal("Time_MonthsAgo(4)", Formatting.Relative(now.AddDays(-125), now, s));
        Assert.Equal("Time_Unknown", Formatting.Relative(null, now, s));
    }

    [Fact]
    public async Task Run_session_dedupes_snapshot_and_trims_to_max_lines()
    {
        var profile = DemoData.Profile("x", "x", RunProfileKind.Run, "test", "C:\\", "x");
        var proc = new DemoRunningProcess("id", profile);
        var session = new RunSessionViewModel(proc, new DemoRunService(new DemoCatalogService(new DemoOptions(true))), new FakeClipboard(), new KeyStrings())
        {
            MaxLines = 3,
        };

        for (var i = 0; i < 5; i++)
        {
            await proc.SendInputAsync("l" + i, CancellationToken.None);
        }

        session.Flush();

        Assert.Equal(3, session.Lines.Count);
        Assert.Equal("(demo) received: l4", session.Lines[^1].Text);
    }

    // ───── resource dictionaries ─────

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

    private static string AppDir => Path.Combine(RepoRoot, "src", "Tezgah.App");

    private static HashSet<string> Keys(string lang)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(Path.Combine(AppDir, "Resources", $"Strings.{lang}.xaml"));
        return [.. doc.Root!.Elements().Select(e => (string)e.Attribute(x + "Key")!)];
    }

    [Fact]
    public void Turkish_and_english_dictionaries_have_the_same_keys()
    {
        var tr = Keys("tr");
        var en = Keys("en");
        Assert.Empty(tr.Except(en));
        Assert.Empty(en.Except(tr));
    }

    [Fact]
    public void Every_string_key_used_in_code_and_xaml_exists()
    {
        var keys = Keys("tr");
        var styleKeys = XDocument.Load(Path.Combine(AppDir, "Resources", "Styles.xaml")).Root!.Descendants()
            .Select(e => (string?)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))).Where(k => k is not null).ToHashSet();
        var used = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(AppDir, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                                 && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in CodeKey().Matches(text))
            {
                used.Add(m.Groups[1].Value);
            }

            foreach (Match m in XamlKey().Matches(text))
            {
                used.Add(m.Groups[1].Value);
            }
        }

        // dynamic prefixes: "Kind_" + enum, "ProfileKind_" + enum, "StepKind_" + enum
        foreach (var k in Enum.GetNames<ItemKind>())
        {
            used.Add("Kind_" + k);
        }

        foreach (var k in Enum.GetNames<RunProfileKind>())
        {
            used.Add("ProfileKind_" + k);
        }

        foreach (var k in Enum.GetNames<UninstallStepKind>())
        {
            used.Add("StepKind_" + k);
        }

        var fluentKeys = used.Where(k => k.EndsWith("Brush", StringComparison.Ordinal)).ToList();
        var missing = used.Except(keys).Except(styleKeys!).Except(fluentKeys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "Missing string keys: " + string.Join(", ", missing));
    }

    [GeneratedRegex("""\.(?:Text|Format)\("([A-Za-z]+_[A-Za-z_]+|Copied)"[,)]""")]
    private static partial Regex CodeKey();

    [GeneratedRegex("""DynamicResource ([A-Za-z]+_[A-Za-z_]+)\}""")]
    private static partial Regex XamlKey();
}
