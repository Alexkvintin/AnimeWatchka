using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Providers;

namespace AnimeApp.Providers.AniDub;

public sealed class AniDubProvider(HttpClient http) : IAnimeProvider
{
    public string Id => "anidub";
    public string DisplayName => "AniDub";
    public string Language => "ru";
    public string Url => "https://online.anidub.com";
    public ProviderCapabilities Features => ProviderCapabilities.Search | ProviderCapabilities.Episodes |
        ProviderCapabilities.DirectPlayback | ProviderCapabilities.Poster | ProviderCapabilities.QualitySelection;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Html)> cache = new();
    private async Task<string> Html(string url, CancellationToken ct)
    {
        if (cache.TryGetValue(url, out var saved) && DateTimeOffset.UtcNow - saved.At < TimeSpan.FromMinutes(2))
            return saved.Html;
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException($"AniDub: сервер видео недоступен (HTTP {(int)response.StatusCode}). Попробуйте другой источник.");
        var html = await response.Content.ReadAsStringAsync(ct);
        cache[url] = (DateTimeOffset.UtcNow, html);
        return html;
    }
    private static Uri ReleaseUri(string id)
    {
        if (!Uri.TryCreate(id, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "online.anidub.com" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
            !uri.AbsolutePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            throw new ProviderException("Некорректный релиз AniDub.");
        return uri;
    }
    private static bool PlayerUri(string value, out Uri uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri!) && uri.Scheme == "https" &&
            uri.UserInfo.Length == 0 && uri.IsDefaultPort &&
            (Regex.IsMatch(uri.Host, @"^player\d*\.ladonyvesna2005\.info$") || uri.Host == "video.sibnet.ru");
    }
    public async Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default)
    {
        try { await Html(Url, ct); return new(true, "Каталог доступен"); }
        catch (Exception e) when (e is HttpRequestException or IOException) { return new(false, e.Message); }
    }
    public async Task<IReadOnlyList<ProviderRelease>> Search(string query, CancellationToken ct = default)
        => ParseSearch(await Html(Url + "/index.php?do=search&subaction=search&story=" + Uri.EscapeDataString(query), ct));
    public static IReadOnlyList<ProviderRelease> ParseSearch(string html)
    {
        using var doc = new HtmlParser().ParseDocument(html);
        var result = new List<ProviderRelease>();
        foreach (var card in doc.QuerySelectorAll("a.th-in"))
        {
            var link = card.GetAttribute("href") ?? "";
            try { ReleaseUri(link); } catch (ProviderException) { continue; }
            var title = card.QuerySelector(".th-title")?.TextContent.Trim() ?? "";
            if (title.Length == 0) continue;
            var image = card.QuerySelector("img")?.GetAttribute("src") ?? "";
            var yearText = Regex.Match(card.QuerySelector(".th-subtitle")?.TextContent ?? "", @"\b(?:19|20)\d{2}\b").Value;
            result.Add(new(link, title, link, int.TryParse(yearText, out var year) ? year : null,
                Poster: Uri.TryCreate(new Uri(link), image, out var poster) && poster.Scheme == "https" ? poster.ToString() : ""));
        }
        return result.DistinctBy(x => x.Id).ToArray();
    }
    public async Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default)
    {
        var uri = ReleaseUri(id);
        using var doc = new HtmlParser().ParseDocument(await Html(uri.ToString(), ct));
        var title = doc.QuerySelector("h1, .fheader h2, h2")?.TextContent.Trim() ?? "";
        if (title.Length == 0) throw new ProviderException("AniDub не вернул карточку релиза.");
        var image = doc.QuerySelector(".fposter img")?.GetAttribute("src") ?? "";
        var yearText = doc.QuerySelector("a[href*='/xfsearch/year/']")?.TextContent.Trim();
        var count = Regex.Match(title, @"\[(\d+)\s+из\s+(\d+)\]", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\s*\[\d+\s+из\s+\d+\].*$", "", RegexOptions.IgnoreCase).Trim();
        return new(id, title, id, int.TryParse(yearText, out var year) ? year : null,
            EpisodesTotal: count.Success ? int.Parse(count.Groups[2].Value) : null,
            EpisodesAvailable: count.Success ? int.Parse(count.Groups[1].Value) : null,
            Poster: Uri.TryCreate(uri, image, out var poster) && poster.Scheme == "https" ? poster.ToString() : "");
    }
    public static IReadOnlyList<ProviderEpisode> ParsePlaylist(string html, string releaseId, string page)
    {
        var match = Regex.Match(html, @"window\.PLAYER_CONTEXT\s*=\s*(\{[^\r\n]+\})\s*;",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!match.Success) return [];
        try
        {
            using var json = JsonDocument.Parse(match.Groups[1].Value);
            if (!json.RootElement.TryGetProperty("episodes", out var episodes) || episodes.ValueKind != JsonValueKind.Array) return [];
            var result = new List<ProviderEpisode>();
            foreach (var item in episodes.EnumerateArray())
            {
                var link = item.TryGetProperty("link", out var l) ? l.GetString() ?? "" : "";
                var label = item.TryGetProperty("label", out var t) ? t.GetString() ?? "" : "";
                if (!Regex.IsMatch(link, @"^/s\d+/[a-f0-9]+$")) continue;
                var number = int.TryParse(Regex.Match(label, @"^\d+").Value, out var n) ? n : result.Count + 1;
                result.Add(new(link, number, label.Length > 0 ? label : $"Серия {number}", releaseId, page));
            }
            return result.DistinctBy(x => x.Id).ToArray();
        }
        catch (JsonException) { return []; }
    }
    public async Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default)
    {
        ReleaseUri(id);
        using var doc = new HtmlParser().ParseDocument(await Html(id, ct));
        var pages = doc.QuerySelectorAll("span[data]").Select(x => (Page: x.GetAttribute("data") ?? "", Label: x.TextContent.Trim()))
            .Where(x => PlayerUri(x.Page, out _)).DistinctBy(x => x.Page).ToList();
        var frame = doc.QuerySelector("#playeriframe")?.GetAttribute("src") ?? "";
        if (pages.Count == 0 && PlayerUri(frame, out _)) pages.Add((frame, "Серия 1"));
        if (pages.Count == 0) throw new ProviderException("Этот плеер AniDub пока не поддерживается. Выберите другой источник.");
        if (pages[0].Page.Contains(".ladonyvesna2005.info/"))
        {
            var episodes = ParsePlaylist(await Html(pages[0].Page, ct), id, pages[0].Page);
            if (episodes.Count > 0) return episodes;
        }
        return pages.Select((p, i) => new ProviderEpisode(p.Page, i + 1, p.Label, id, p.Page)).ToArray();
    }
    public async Task<PlaybackSource> GetPlayback(string releaseId, ProviderEpisode episode, CancellationToken ct = default)
    {
        var episodes = await GetEpisodes(releaseId, ct);
        var selected = episodes.FirstOrDefault(x => x.Id == episode.Id)
            ?? throw new ProviderException("Серия не найдена в релизе AniDub.");
        if (!PlayerUri(selected.PageUrl, out var page)) throw new ProviderException("Некорректный адрес плеера AniDub.");
        if (page.Host == "video.sibnet.ru")
        {
            var location = ParseSibnet(await Html(page.ToString(), ct), page);
            if (location is null) throw new ProviderException("Sibnet не предоставил прямое видео. Выберите другую студию.");
            return new(PlaybackKind.DirectMedia, location, new Dictionary<string, string> { ["Referer"] = page.ToString() });
        }
        if (!Regex.IsMatch(selected.Id, @"^/s\d+/[a-f0-9]+$"))
            throw new ProviderException("AniDub не предоставил прямой поток этой серии.");
        var stream = new Uri(page, "vid.php?v=" + Uri.EscapeDataString(selected.Id));
        using var request = new HttpRequestMessage(HttpMethod.Get, stream);
        request.Headers.Referrer = page;
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException($"AniDub: видео недоступно (HTTP {(int)response.StatusCode}).");
        var manifest = await response.Content.ReadAsStringAsync(ct);
        if (!manifest.TrimStart().StartsWith("#EXTM3U")) throw new ProviderException("AniDub не вернул видеопоток.");
        var variants = manifest.Contains("#EXT-X-MEDIA:") ? [] : QualityNegotiation.ParseMaster(manifest, stream);
        return new(PlaybackKind.DirectMedia, QualityNegotiation.Select(variants)?.Location ?? stream.ToString(),
            new Dictionary<string, string> { ["Referer"] = page.ToString() }, variants);
    }
    public static string? ParseSibnet(string html, Uri page)
    {
        var match = Regex.Match(html, """player\.src\s*\(\s*\[\s*\{\s*src\s*:\s*["']([^"']+)["']""",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!match.Success || !Uri.TryCreate(page, match.Groups[1].Value, out var uri) ||
            uri.Scheme != "https" || uri.Host != "video.sibnet.ru" || uri.UserInfo.Length > 0 ||
            !uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return null;
        return uri.ToString();
    }
}
