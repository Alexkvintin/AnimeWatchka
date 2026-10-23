using System.Globalization;

namespace AnimeApp.Services.Playback;
public static class MediaLanguage
{
    public static string ForCulture(CultureInfo culture) => culture.TwoLetterISOLanguageName switch { "uk" => "uk", "ru" => "ru", _ => "en" };
    public static string Default => ForCulture(CultureInfo.CurrentUICulture);
    public static string ProviderOrder => Default + ",en,any";
    public static bool MatchesTrack(string label) => System.Text.RegularExpressions.Regex.IsMatch(label, Default switch {
        "uk" => @"(?i)(\buk\b|\bukr\b|ukrainian|укра)",
        "ru" => @"(?i)(\bru\b|\brus\b|russian|рус)",
        _ => @"(?i)(\ben\b|\beng\b|english|англ)"
    });
}
