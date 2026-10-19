using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Playback;

namespace AnimeApp.Providers.AnimeVost;
public record AnimeVostSearchResult(string Id, string Title, string Url, int? Year = null, string Kind = "", int? Total = null);
public record AnimeVostRelease(ProviderRelease Release, IReadOnlyList<ProviderEpisode> Episodes, string FrameEndpoint);
public record AnimeVostEpisode(string Id, int Number, string Title, string ReleaseId, string PageUrl);
public interface IAnimeVostParser
{
    IReadOnlyList<AnimeVostSearchResult> SearchResults(string html);
    AnimeVostRelease Release(string html, string url);
    IReadOnlyList<MediaVariant> Media(string html, Uri page);
    string? Frame(string html, Uri page);
    string? LegacyFrame(string html, Uri page) => null;
}

public sealed class AnimeVostParser : IAnimeVostParser
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);
    private static Match Match(string text, string pattern) => Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, Limit);
    private static string Clean(string title) => Regex.Replace(WebUtility.HtmlDecode(title), @"\s*\[[^\]]*\]", "", RegexOptions.None, Limit).Trim();
    private static int? Int(string s) => int.TryParse(s, out var n) ? n : null;
    public IReadOnlyList<AnimeVostSearchResult> SearchResults(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        return doc.QuerySelectorAll("h2 a[href], .shortstoryHead a[href], article h3 a[href]").Select(a => new { Title = a.TextContent.Trim(), Url = Uri.TryCreate(new Uri("https://animevost.org"), a.GetAttribute("href"), out var u) ? u : null }).Where(a => a.Url is { Scheme: "https", Host: "animevost.org" } && a.Url.AbsolutePath.EndsWith(".html")).Select(a => new AnimeVostSearchResult(a.Url!.ToString(), Clean(a.Title), a.Url.ToString(), null, Kind(a.Url.AbsolutePath), Int(Match(a.Title, @"из\s+(\d+)").Groups[1].Value))).DistinctBy(a => a.Id).ToList();
    }

    private static string Kind(string url) => url.Contains("polnometrazhnyy") ? "movie" : url.Contains("/ova/") ? "ova" : url.Contains("special") || url.Contains("speshl") ? "special" : url.Contains("/tv/") ? "tv" : "";
    public AnimeVostRelease Release(string html, string url)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var header = doc.QuerySelector(".shortstoryHead h1, .shortstoryHead h2, h1")?.TextContent.Trim();
        if (string.IsNullOrEmpty(header))
            throw new ProviderException("Не удалось прочитать релиз. Возможно, структура страницы AnimeVost изменилась.");
        var body = doc.Body?.TextContent ?? "";
        var year = Int(Match(body, @"Год выхода:\s*(\d{4})").Groups[1].Value);
        var total = Int(Match(body, @"Количество серий:\s*(\d+)").Groups[1].Value);
        var endpoint = Regex.Matches(html, @"[""'](?<path>/frame\d*\.php\?play=)[""']", RegexOptions.None, Limit).Select(m => m.Groups["path"].Value).FirstOrDefault(x => x.Contains("frame2"));
        var data = Match(string.Join("\n", doc.Scripts.Select(s => s.TextContent)), @"\b(?:var|let|const)\s+data\s*=\s*(\{[^{}]*\})\s*;");
        if (!data.Success)
            throw new ProviderException("Не удалось получить список серий. Возможно, структура страницы изменилась.");
        var episodes = new List<ProviderEpisode>();
        try
        {
            using var json = JsonDocument.Parse(data.Groups[1].Value, new JsonDocumentOptions { AllowTrailingCommas = true });
            foreach (var p in json.RootElement.EnumerateObject())
            {
                var id = p.Value.ToString();
                if (!Regex.IsMatch(id, @"^\d+$", RegexOptions.None, Limit))
                    continue;
                var label = p.Name;
                var number = Int(Match(label, @"^(\d+)\s*(?:сер|эпиз|$)").Groups[1].Value);
                var movie = label.Equals("Фильм", StringComparison.OrdinalIgnoreCase);
                // Specials without a canonical series number never advance the main-series counter.
                episodes.Add(new(id, number ?? (movie ? 1 : 0), label, url, url, true, number.HasValue || movie));
            }
        }
        catch (JsonException ex)
        {
            throw new ProviderException("Список серий AnimeVost повреждён или изменился.", ex);
        }

        if (episodes.Count == 0)
            throw new ProviderException("У релиза пока нет доступных серий.");
        if (endpoint is null)
            throw new ProviderException("Не найден адрес штатного плеера AnimeVost. Структура страницы изменилась.");
        var poster = doc.QuerySelector(".shortstoryContent img.imgRadius, meta[property='og:image']");
        var posterPath = poster?.GetAttribute("src") ?? poster?.GetAttribute("content");
        var posterUrl = Uri.TryCreate(new Uri(url), posterPath, out var image) && image.Scheme == "https" ? image.ToString() : "";
        var release = new ProviderRelease(url, Clean(header), url, year, Kind(url), total, episodes.Count, posterUrl);
        return new(release, episodes.DistinctBy(e => e.Id).ToList(), endpoint);
    }

    public string? Frame(string html, Uri page)
    {
        var doc = new HtmlParser().ParseDocument(html);
        return doc.QuerySelectorAll("iframe[src]").Select(e => Uri.TryCreate(page, e.GetAttribute("src"), out var u) ? u : null).FirstOrDefault(u => u is not null && u.Scheme == "https" && u.Host == page.Host)?.ToString();
    }

    public IReadOnlyList<MediaVariant> Media(string html, Uri page)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var result = new List<MediaVariant>();
        foreach (var e in doc.QuerySelectorAll("a[href],video[src],source[src]"))
        {
            var value = e.GetAttribute("href") ?? e.GetAttribute("src");
            if (!Uri.TryCreate(page, value, out var uri) || uri.Scheme != "https" || !Regex.IsMatch(uri.AbsolutePath, @"\.(mp4|m3u8|webm)$", RegexOptions.IgnoreCase, Limit))
                continue;
            var hint = string.Join(" ", e.TextContent, e.GetAttribute("label"), e.GetAttribute("data-res"), e.GetAttribute("res"), uri.AbsolutePath);
            var quality = Int(Match(hint, @"(?:^|\D)(2160|1440|1080|720|576|480|360|240)(?:p|\D|$)").Groups[1].Value);
            result.Add(new(quality is int q ? q + "p" : "Auto", uri.ToString()));
        }

        // PlayerJS lists alternate servers with "or". A 720p fallback inside a
        // 1080p group must never be advertised as a real 1080p stream.
        foreach (var script in doc.Scripts)
            foreach (Match file in Regex.Matches(script.TextContent, "\"file\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\")\\s*[,}]", RegexOptions.None, Limit))
            {
                string playlist;
                try
                {
                    playlist = JsonSerializer.Deserialize<string>(file.Groups[1].Value) ?? "";
                }
                catch (JsonException)
                {
                    continue;
                }

                foreach (Match group in Regex.Matches(playlist, @"\[[^\]]*?(?<q>1080|720|480)[pр][^\]]*\](?<urls>[^\[]*)", RegexOptions.IgnoreCase, Limit))
                {
                    var quality = int.Parse(group.Groups["q"].Value);
                    var first = group.Groups["urls"].Value.Split(" or ", StringSplitOptions.TrimEntries)[0].Trim().TrimEnd(',');
                    if (!Uri.TryCreate(first, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var pathQuality = Int(Match(uri.AbsolutePath, @"/(1080|720|480)/").Groups[1].Value);
                    if (pathQuality.HasValue && pathQuality != quality)
                        continue;
                    result.Add(new(quality + "p", uri.ToString()));
                }
            }

        return result.OrderByDescending(v => v.Height).DistinctBy(v => v.Location).ToList();
    }

    public string? LegacyFrame(string html, Uri page)
    {
        // Follow the fallback explicitly published by the page; do not invent endpoints.
        foreach (Match match in Regex.Matches(html, @"document\.location\.href\s*=\s*['""](?<url>/frame\d*\.php\?[^'""]+)['""]", RegexOptions.None, Limit))
        {
            if (Uri.TryCreate(page, WebUtility.HtmlDecode(match.Groups["url"].Value), out var uri) && uri.Scheme == "https" && uri.Host == page.Host && uri.AbsolutePath == page.AbsolutePath && uri.Query.Split('&').Any(p => p == "old=1") && uri != page)
                return uri.ToString();
        }

        return null;
    }
}
