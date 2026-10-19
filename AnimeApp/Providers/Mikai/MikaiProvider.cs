using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AnimeApp.Models;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Providers;

namespace AnimeApp.Providers.Mikai;

public sealed class MikaiClient(HttpClient http)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, JsonElement Value)> cache = new();
    private readonly SemaphoreSlim gate = new(1);
    private DateTimeOffset next;
    public async Task<JsonElement> Get(string path, CancellationToken ct)
    {
        if (cache.TryGetValue(path, out var ready) && DateTimeOffset.UtcNow - ready.At < TimeSpan.FromMinutes(5)) return ready.Value;
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(path, out var saved) && DateTimeOffset.UtcNow - saved.At < TimeSpan.FromMinutes(5)) return saved.Value;
            var wait = next - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.mikai.me/public/v1/" + path);
            request.Headers.UserAgent.ParseAdd("AnimeWatchka/0.1.1");
            using var response = await http.SendAsync(request, ct);
            next = DateTimeOffset.UtcNow.AddSeconds(1.1);
            if ((int)response.StatusCode == 429)
            {
                next = DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                throw new ProviderException("Mikai просит подождать перед следующим запросом.");
            }
            if (!response.IsSuccessStatusCode) throw new ProviderException("Mikai временно недоступен.");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.GetProperty("ok").GetBoolean()) throw new ProviderException("Mikai не предоставил данные.");
            var value = doc.RootElement.Clone();
            cache[path] = (DateTimeOffset.UtcNow, value);
            return value;
        }
        finally { gate.Release(); }
    }
    public async Task<PlaybackSource> ResolveVideo(string page, CancellationToken ct)
    {
        if (!Uri.TryCreate(page, UriKind.Absolute, out var origin) || origin.Scheme != "https" || origin.Host != "ashdi.vip")
            throw new ProviderException("Для этой серии пока нет прямого видеопотока.");
        var html = await http.GetStringAsync(origin, ct);
        var location = AshdiMedia(html) ?? throw new ProviderException("Источник не предоставил прямое видео. Попробуйте другую серию или озвучку.");
        using var request = new HttpRequestMessage(HttpMethod.Get, location);
        request.Headers.Referrer = origin;
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var manifest = await response.Content.ReadAsStringAsync(ct);
        if (!manifest.TrimStart().StartsWith("#EXTM3U")) throw new ProviderException("Источник вернул некорректный видеопоток.");
        var variants = manifest.Contains("#EXT-X-MEDIA:") ? [] : QualityNegotiation.ParseMaster(manifest, new Uri(location));
        return new(PlaybackKind.DirectMedia, QualityNegotiation.Select(variants)?.Location ?? location,
            new Dictionary<string, string> { ["Referer"] = page }, variants);
    }
    public static string? AshdiMedia(string html)
    {
        // Read only the main PlayerJS file field, never advertising URLs or scripts.
        var match = System.Text.RegularExpressions.Regex.Match(html,
            "new\\s+Playerjs\\s*\\(\\s*\\{[^}]*?\\bfile\\s*:\\s*['\"](?<url>https://[^'\"\\s]+)['\"]",
            System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        var value = System.Net.WebUtility.HtmlDecode(match.Groups["url"].Value).Replace("\\/", "/");
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            (uri.Host == "ashdi.vip" || uri.Host.EndsWith(".ashdi.vip", StringComparison.OrdinalIgnoreCase)) &&
            uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? value : null;
    }
}

public sealed class MikaiProvider(MikaiClient client, string team, string name, int teamId) : IAnimeProvider, IScheduleProvider
{
    public string Id => team;
    public string DisplayName => name;
    public string Language => "uk";
    public string Url => "https://mikai.me/team/" + team;
    public string Capabilities => "Українська озвучка · серии · новые выпуски · каталог Mikai (https://mikai.me)";
    public ProviderCapabilities Features => ProviderCapabilities.Search | ProviderCapabilities.Episodes | ProviderCapabilities.Schedule | ProviderCapabilities.DirectPlayback | ProviderCapabilities.QualitySelection;
    internal static string Text(JsonElement e, params string[] path)
    {
        foreach (var key in path) if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out e)) return "";
        return e.ValueKind == JsonValueKind.Null ? "" : e.ToString();
    }
    private static string Key(string id) => long.TryParse(id, out var n) && n > 0 ? id : throw new ProviderException("Некорректный релиз Mikai.");
    public async Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default)
    {
        try { await client.Get("teams/" + team, ct); return new(true, "Доступен"); }
        catch (IOException e) { return new(false, e.Message); }
    }
    public async Task<IReadOnlyList<ProviderRelease>> Search(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var data = await client.Get($"anime?teams={teamId}&search={Uri.EscapeDataString(query.Trim())}&limit=30", ct);
        return data.GetProperty("result").EnumerateArray().Select(ParseRelease).ToArray();
    }
    public async Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default) => ParseRelease((await client.Get("anime/" + Key(id), ct)).GetProperty("result"));
    public static ProviderRelease ParseRelease(JsonElement data)
    {
        var id = Text(data, "ids", "mikai");
        var title = string.Join(" / ", new[] { Text(data, "titles", "ua"), Text(data, "titles", "original"), Text(data, "titles", "english") }.Where(x => x != "").Distinct());
        var total = Text(data, "episodes", "total");
        if (total == "") total = Text(data, "episodes");
        return new(id, title, "https://mikai.me/anime/" + id + "-" + Text(data, "ids", "slug"),
            int.TryParse(Text(data, "year"), out var year) ? year : null, Text(data, "format"),
            int.TryParse(total, out var count) && count > 0 ? count : null,
            Poster: Text(data, "images", "poster", "medium", "jpg"));
    }
    public async Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default) => ParseEpisodes(Key(id), (await client.Get("anime/" + Key(id) + "/player", ct)).GetProperty("result"), team);
    public static IReadOnlyList<ProviderEpisode> ParseEpisodes(string id, JsonElement data, string team)
    {
        if (data.TryGetProperty("licensed", out var licensed) && licensed.ValueKind == JsonValueKind.True) return [];
        var result = new List<ProviderEpisode>();
        foreach (var release in data.GetProperty("releases").EnumerateArray().Where(r => Text(r, "kind") == "voice" && r.GetProperty("teams").EnumerateArray().Any(t => Text(t, "slug") == team)))
        foreach (var episode in release.GetProperty("episodes").EnumerateArray())
        {
            if (!decimal.TryParse(Text(episode, "number"), CultureInfo.InvariantCulture, out var number) || number <= 0) continue;
            var url = episode.GetProperty("sources").EnumerateArray().OrderBy(s => Text(s, "provider") == "ashdi" ? 0 : 1).Select(s => Text(s, "embedUrl")).FirstOrDefault(s => Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == "https");
            if (url is null) continue;
            // Fractional episodes are retained, but cannot advance whole-episode progress.
            result.Add(new(id + ":" + number.ToString(CultureInfo.InvariantCulture), (int)number, "Серия " + Text(episode, "label"), id, url, CountsTowardProgress: number == decimal.Truncate(number)));
        }
        return result.DistinctBy(e => e.Id).OrderBy(e => e.Number).ToArray();
    }
    public async Task<PlaybackSource> GetPlayback(string releaseId, ProviderEpisode episode, CancellationToken ct = default)
    {
        var current = (await GetEpisodes(releaseId, ct)).FirstOrDefault(e => e.Id == episode.Id) ?? throw new ProviderException("Серия этой команды недоступна.");
        return await client.ResolveVideo(current.PageUrl, ct);
    }
    public async Task<IReadOnlyList<ScheduleItem>> Schedule(CancellationToken ct = default)
    {
        var items = new List<ScheduleItem>();
        var ongoing = new HashSet<string>();
        for (var page = 1; ; page++)
        {
            var catalog = await client.Get($"anime?teams={teamId}&statuses=ongoing&limit=100&page={page}", ct);
            foreach (var anime in catalog.GetProperty("result").EnumerateArray()) ongoing.Add(Text(anime, "ids", "mikai"));
            if (!int.TryParse(Text(catalog, "pages"), out var pages) || page >= pages) break;
        }
        var since = DateTimeOffset.UtcNow.AddDays(-7).ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture);
        for (var page = 1; ; page++)
        {
            var response = await client.Get($"updates?since={Uri.EscapeDataString(since)}&limit=100&page={page}", ct);
            foreach (var row in response.GetProperty("result").EnumerateArray().Where(r => Text(r, "kind") == "voice" && r.GetProperty("teams").EnumerateArray().Any(t => Text(t, "slug") == team)))
            {
                if (!ongoing.Contains(Text(row, "anime", "ids", "mikai"))) continue;
                if (!int.TryParse(Text(row, "episode"), out var n) || !DateTimeOffset.TryParse(Text(row, "publishedAt"), out var at)) continue;
                var release = ParseRelease(row.GetProperty("anime"));
                items.Add(new(new Anime { Name = release.Title, Poster = release.Poster }, n, at) { ProviderId = Id, ProviderName = name, Release = release, Released = true, Availability = "✓ Доступна озвучка " + name, TimeHint = "Добавлено на Mikai: " + at.ToLocalTime().ToString("dd.MM HH:mm") });
            }
            if (!int.TryParse(Text(response, "pages"), out var pages) || page >= pages) break;
        }
        return items.DistinctBy(i => (i.Release!.Id, i.ExpectedEpisode)).ToArray();
    }
}
