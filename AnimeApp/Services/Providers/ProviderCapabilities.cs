using AnimeApp.Models;

namespace AnimeApp.Services.Providers;
[Flags]
public enum ProviderCapabilities
{
    None = 0,
    Search = 1,
    Catalog = 2,
    Episodes = 4,
    DirectPlayback = 8,
    WebPlayback = 16,
    Schedule = 32,
    Poster = 64,
    QualitySelection = 128,
    SeparateAudio = 256
}

public static class CapabilityLabels
{
    public static string Describe(ProviderCapabilities flags) => string.Join(" · ", new (ProviderCapabilities Flag, string Name)[] { (ProviderCapabilities.Search, "Поиск"), (ProviderCapabilities.Catalog, "Каталог"), (ProviderCapabilities.Episodes, "Серии"), (ProviderCapabilities.DirectPlayback, "Прямое видео"), (ProviderCapabilities.WebPlayback, "Web"), (ProviderCapabilities.Schedule, "Расписание"), (ProviderCapabilities.Poster, "Постеры"), (ProviderCapabilities.QualitySelection, "Качество"), (ProviderCapabilities.SeparateAudio, "Отдельное аудио") }.Where(x => flags.HasFlag(x.Flag)).Select(x => x.Name));
}

public record FilterChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

public record AnimeSearch(string Query = "", int? FromYear = null, int? ToYear = null, string Kind = "", string Status = "", string Season = "", int Page = 1)
{
    public void Validate()
    {
        if (FromYear is < 1900 or > 2200 || ToYear is < 1900 or > 2200 || FromYear > ToYear)
            throw new ArgumentException("Укажите год от 1900 до 2200; начало диапазона не должно быть позже конца.");
        if (!new[]
        {
            "",
            "tv",
            "movie",
            "ova",
            "ona",
            "special",
            "tv_special"
        }.Contains(Kind) || !new[]
        {
            "",
            "anons",
            "ongoing",
            "released"
        }.Contains(Status) || !new[]
        {
            "",
            "winter",
            "spring",
            "summer",
            "fall"
        }.Contains(Season))
            throw new ArgumentException("Недопустимый фильтр каталога.");
        if (Season != "" && FromYear is null && ToYear is null)
            throw new ArgumentException("Для сезона укажите год или диапазон лет.");
    }

    public string ToShikimoriQuery()
    {
        Validate();
        var p = new List<string>
        {
            "limit=50",
            "page=" + Math.Max(1, Page),
            "order=ranked"
        };
        if (Query.Trim() != "")
            p.Add("search=" + Uri.EscapeDataString(Query.Trim()));
        if (Kind != "")
            p.Add("kind=" + (Kind == "special" ? "special,tv_special" : Kind));
        if (Status != "")
            p.Add("status=" + Status);
        if (FromYear is not null || ToYear is not null)
        {
            var from = FromYear ?? ToYear!.Value;
            var to = ToYear ?? from;
            p.Add("season=" + string.Join(",", Enumerable.Range(from, to - from + 1).Select(y => Season == "" ? y.ToString() : Season + "_" + y)));
        }

        return string.Join("&", p);
    }

    public bool Matches(Anime a) => ((FromYear ?? ToYear)is not int from || a.Year >= from) && ((ToYear ?? FromYear)is not int to || a.Year <= to) && (Kind == "" || a.Kind == Kind || Kind == "special" && a.Kind == "tv_special") && (Status == "" || a.Status == Status) && (Season == "" || a.AirMonth is int m && Season == (m <= 3 ? "winter" : m <= 6 ? "spring" : m <= 9 ? "summer" : "fall"));
}
