using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Playback;
namespace AnimeApp.Providers.Uakino;
public sealed class UakinoProvider(Func<string, bool, CancellationToken, Task<string>> load) : IAnimeProvider
{
    public string Id => "uakino";
    public string DisplayName => "UAKino";
    public string Language => "uk";
    public string Url => "https://uakino.best/animeukr/";
    public ProviderCapabilities Features => ProviderCapabilities.Search | ProviderCapabilities.Episodes | ProviderCapabilities.WebPlayback;
    public string Capabilities => "Поиск · серии и озвучки · встроенный веб-плеер";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, string Html)> cache = new();
    private async Task<string> Page(string url, bool episodes, CancellationToken ct)
    {
        Validate(url);
        var key = url + episodes;
        if (cache.TryGetValue(key, out var saved) && DateTimeOffset.UtcNow - saved.At < TimeSpan.FromMinutes(5)) return saved.Html;
        var html = await load(url, episodes, ct); cache[key] = (DateTimeOffset.UtcNow, html); return html;
    }
    private static void Validate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host != "uakino.best") throw new ProviderException("Недопустимая ссылка UAKino.");
    }
    public async Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default)
    {
        try { await Page(Url, false, ct); return new(true, "UAKino доступен"); }
        catch (IOException ex) { return new(false, ex.Message); }
    }
    public async Task<IReadOnlyList<ProviderRelease>> Search(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var url = "https://uakino.best/ua/?do=search&subaction=search&story=" + Uri.EscapeDataString(query.Trim());
        return ParseSearch(await Page(url, false, ct));
    }
    public static IReadOnlyList<ProviderRelease> ParseSearch(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        return doc.QuerySelectorAll("#dle-content a.movie-title, #dle-content .short-title a").Select(a =>
        {
            var url = new Uri(new Uri("https://uakino.best/"), a.GetAttribute("href") ?? "");
            return new ProviderRelease(url.AbsoluteUri, a.TextContent.Trim(), url.AbsoluteUri);
        }).Where(r => new Uri(r.Url).Host == "uakino.best" && new Uri(r.Url).AbsolutePath.StartsWith("/animeukr/") && r.Url.EndsWith(".html")).DistinctBy(r => r.Id).ToArray();
    }
    public async Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default) => ParseRelease(id, await Page(id, false, ct));
    public static ProviderRelease ParseRelease(string id, string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var title = doc.QuerySelector("h1")?.TextContent.Trim() ?? "";
        var original = Regex.Replace(doc.QuerySelector(".origintitle")?.TextContent ?? "", @"\s+\d+\s+season\s*$", "", RegexOptions.IgnoreCase).Trim();
        var yearText = doc.QuerySelector("a[href*='/find/year/']")?.TextContent;
        var poster = doc.QuerySelector(".movie-poster img, .film-poster img, .full-poster img, meta[property='og:image']");
        var posterUrl = poster?.GetAttribute("src") ?? poster?.GetAttribute("content") ?? "";
        return new(id, title + (original == "" ? "" : " / " + original), id, int.TryParse(yearText, out var year) ? year : null, id.Contains("anime-series") ? "tv" : id.Contains("anime-solo") ? "movie" : "", Poster: posterUrl == "" ? "" : new Uri(new Uri(id), posterUrl).AbsoluteUri);
    }
    public async Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default) => ParseEpisodes(id, await Page(id, true, ct));
    public static IReadOnlyList<ProviderEpisode> ParseEpisodes(string release, string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var result = new List<ProviderEpisode>();
        foreach (var row in doc.QuerySelectorAll(".playlists-videos [data-file]"))
        {
            var number = Regex.Match(row.TextContent, @"\d+");
            if (!int.TryParse(number.Value, out var n) || n < 1 || !Uri.TryCreate(new Uri(release), row.GetAttribute("data-file"), out var link) || link.Scheme != "https") continue;
            var voice = row.GetAttribute("data-voice") ?? "";
            result.Add(new(link.AbsoluteUri, n, $"Серия {n} · {voice}", release, link.AbsoluteUri));
        }
        return result.DistinctBy(e => e.Id).OrderBy(e => e.Number).ThenBy(e => e.Title.Contains("Субтитри")).ToArray();
    }
    public async Task<PlaybackSource> GetPlayback(string releaseId, ProviderEpisode episode, CancellationToken ct = default)
    {
        var actual = (await GetEpisodes(releaseId, ct)).FirstOrDefault(e => e.Id == episode.Id) ?? throw new ProviderException("Серия больше не доступна.");
        return new(PlaybackKind.WebPage, actual.PageUrl);
    }
}
