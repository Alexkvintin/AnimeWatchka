using AngleSharp.Html.Parser;
using AnimeApp.Models;
using AnimeApp.Services.Providers;
using System.Text.RegularExpressions;

namespace AnimeApp.Providers.AnimeVost;
public sealed partial class AnimeVostProvider
{
    private IReadOnlyList<ScheduleItem>? scheduleCache;
    private DateTimeOffset scheduleAt;
    private readonly SemaphoreSlim scheduleGate = new(1, 1);
    public static IReadOnlyList<ScheduleItem> ParseScheduleLinks(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var items = new List<ScheduleItem>();
        var ids = new[]
        {
            "Mon",
            "Tue",
            "Wed",
            "Thu",
            "Fri",
            "Sat",
            "Sun"
        };
        for (int day = 0; day < 7; day++)
            foreach (var a in doc.QuerySelectorAll("#raspis" + ids[day] + " a[href]"))
            {
                if (!Uri.TryCreate(new Uri("https://animevost.org"), a.GetAttribute("href"), out var uri) || uri.Scheme != "https" || uri.Host != "animevost.org" || !uri.AbsolutePath.EndsWith(".html"))
                    continue;
                var text = a.TextContent.Trim();
                var title = text.Split('~')[0].Trim();
                var hint = text.Contains('~') ? "Ожидается примерно " + text.Split('~')[1].Trim() + " · время сайта, Москва" : "Время не указано";
                items.Add(new(new Anime { Name = title }, 0, null) { ProviderId = "animevost", ProviderName = "AnimeVost", ProviderDay = (DayOfWeek)((day + 1) % 7), Release = new(uri.ToString(), title, uri.ToString()), TimeHint = hint, Availability = "Наличие серии проверяется" });
            }

        return items.DistinctBy(i => (i.Release!.Id, i.ProviderDay)).ToArray();
    }

    public async Task<IReadOnlyList<ScheduleItem>> Schedule(CancellationToken ct = default)
    {
        await scheduleGate.WaitAsync(ct);
        try
        {
            if (scheduleCache is not null && DateTimeOffset.UtcNow - scheduleAt < TimeSpan.FromMinutes(5))
                return scheduleCache;
            var links = ParseScheduleLinks(await Fetch(Url, ct));
            using var throttle = new SemaphoreSlim(3);
            var tasks = links.Select(async item =>
            {
                await throttle.WaitAsync(ct);
                try
                {
                    var html = await Fetch(item.Release!.Id, ct);
                    var doc = new HtmlParser().ParseDocument(html);
                    var header = doc.QuerySelector(".shortstoryHead h1, .shortstoryHead h2")?.TextContent.Trim() ?? item.Anime.Title;
                    var title = Regex.Replace(header, @"\s*\[[^\]]*\]", "").Trim();
                    var poster = doc.QuerySelector(".shortstoryContent img.imgRadius")?.GetAttribute("src");
                    var posterUrl = Uri.TryCreate(new Uri(Url), poster, out var image) && image.Scheme == "https" ? image.ToString() : "";
                    var body = doc.QuerySelector(".shortstoryContent")?.TextContent ?? "";
                    var year = Regex.Match(body, @"Год выхода:\s*(\d{4})");
                    var release = item.Release with
                    {
                        Title = title,
                        Poster = posterUrl,
                        Year = year.Success ? int.Parse(year.Groups[1].Value) : null,
                        Kind = item.Release.Id.Contains("/ona/") ? "ona" : "tv"
                    };
                    int available = 0;
                    try
                    {
                        var parsed = parser.Release(html, item.Release.Id);
                        release = parsed.Release;
                        available = parsed.Episodes.Where(e => e.Available && e.CountsTowardProgress).Select(e => e.Number).DefaultIfEmpty().Max();
                        cache[item.Release.Id] = (DateTimeOffset.UtcNow, parsed);
                    }
                    catch (ProviderException)
                    {
                    }

                    var next = Regex.Match(header, @"\[(\d+)\s*серия\s*[-—]([^\]]+)\]");
                    var expected = next.Success ? int.Parse(next.Groups[1].Value) : available;
                    var ready = available > 0 && expected <= available;
                    var published = doc.QuerySelector(".staticInfoLeftData")?.TextContent.Trim();
                    return item with
                    {
                        Release = release,
                        Anime = item.Anime with
                        {
                            Name = release.Title,
                            Poster = release.Poster,
                            Year = release.Year,
                            Kind = release.Kind
                        },
                        ExpectedEpisode = expected,
                        Released = ready,
                        TimeHint = ready ? "Последняя доступная серия" + (string.IsNullOrWhiteSpace(published) ? "" : " · публикация: " + published) : next.Success ? "Ожидается " + next.Groups[2].Value.Trim() + " · " + item.TimeHint : item.TimeHint,
                        Availability = ready ? "✓ Доступна у AnimeVost" : available > 0 ? $"Ожидается · уже доступны серии 1–{available}" : "Ожидается · доступных серий пока нет"
                    };
                }
                catch (IOException)
                {
                    return item with
                    {
                        Availability = "Не удалось проверить серии"
                    };
                }
                finally
                {
                    throttle.Release();
                }
            });
            scheduleCache = await Task.WhenAll(tasks);
            scheduleAt = DateTimeOffset.UtcNow;
            return scheduleCache;
        }
        finally
        {
            scheduleGate.Release();
        }
    }
}
