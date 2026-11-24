using System.Globalization;
using Tezgah.App.Services;

namespace Tezgah.App.ViewModels;

public enum ItemFilter
{
    All,
    Repos,
    Apps,
    Tools,
    AiMcp,
    Running,
    Forgotten,
    Favorites,
}

public enum SortMode
{
    Activity,
    Name,
    Size,
    Source,
}

public static class Formatting
{
    /// <summary>
    /// Culture for numbers/dates shown in the UI. Explicit (instead of CultureInfo.CurrentCulture) because culture
    /// changes made inside async methods are reverted with the ExecutionContext.
    /// </summary>
    public static CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    public static string Bytes(long? bytes)
    {
        if (bytes is not { } b || b < 0)
        {
            return string.Empty;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = b;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        var format = u == 0 || v >= 100 ? "0" : "0.#";
        return v.ToString(format, Culture) + " " + units[u];
    }

    public static int DaysSince(DateTimeOffset? at, DateTimeOffset now) =>
        at is { } t ? Math.Max(0, (int)(now - t).TotalDays) : 0;

    public static string Relative(DateTimeOffset? at, DateTimeOffset now, IStrings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (at is not { } t)
        {
            return s.Text("Time_Unknown");
        }

        var d = now - t;
        if (d < TimeSpan.FromMinutes(1))
        {
            return s.Text("Time_JustNow");
        }

        if (d < TimeSpan.FromHours(1))
        {
            return s.Format("Time_MinutesAgo", (int)d.TotalMinutes);
        }

        if (d < TimeSpan.FromDays(1))
        {
            return s.Format("Time_HoursAgo", (int)d.TotalHours);
        }

        if (d < TimeSpan.FromDays(2))
        {
            return s.Text("Time_Yesterday");
        }

        if (d < TimeSpan.FromDays(60))
        {
            return s.Format("Time_DaysAgo", (int)d.TotalDays);
        }

        if (d < TimeSpan.FromDays(730))
        {
            return s.Format("Time_MonthsAgo", (int)(d.TotalDays / 30));
        }

        return s.Format("Time_YearsAgo", (int)(d.TotalDays / 365));
    }

    /// <summary>Turns a git remote (https or scp-like ssh) into a browsable https URL. Null if not convertible.</summary>
    public static Uri? RemoteToWeb(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote))
        {
            return null;
        }

        var r = remote.Trim();
        if (r.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            // git@github.com:user/repo.git
            var colon = r.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                return null;
            }

            r = "https://" + r[4..colon] + "/" + r[(colon + 1)..];
        }
        else if (r.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            r = "https://" + r[6..].Replace("git@", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        if (r.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            r = r[..^4];
        }

        return Uri.TryCreate(r, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri
            : null;
    }

    /// <summary>
    /// Case-, dotted/dotless-I- and diacritic-insensitive form used by search, independent of the UI culture: in tr-TR a
    /// culture-aware IgnoreCase compare treats 'I' and 'i' as different letters, so "intellij" never found "IntelliJ".
    /// </summary>
    public static string SearchFold(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(ch switch
            {
                'I' or 'ı' or 'İ' => 'i',
                _ => char.ToLowerInvariant(ch),
            });
        }

        return sb.ToString();
    }
}
