using System.Globalization;

namespace Tezgah.Core;

/// <summary>
/// Minimal bilingual text helper for user-facing strings produced by the core
/// (plan warnings, step descriptions, errors). Picks Turkish when the current UI culture is Turkish.
/// Usage: <c>L.T("Klasör Geri Dönüşüm Kutusu'na taşınacak", "Folder will be moved to the Recycle Bin")</c>.
/// </summary>
public static class L
{
    public static bool IsTurkish => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";

    public static string T(string tr, string en) => IsTurkish ? tr : en;
}
