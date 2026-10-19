using System.Collections.Concurrent;
using System.Text.Json;
using AnimeApp.Models;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Playback;

namespace AnimeApp.Providers.AniLibria;
public sealed class AniLibriaProvider(HttpClient http) : IAnimeProvider, IScheduleProvider
{
    public const string BaseUrl = "https://aniliberty.top";
    public string Id => "anilibria";
    public string DisplayName => "AniLibria / AniLiberty";
    public string Language => "ru";
    public string Url => BaseUrl;
    public ProviderCapabilities Features => ProviderCapabilities.Search | ProviderCapabilities.Catalog | ProviderCapabilities.Episodes | ProviderCapabilities.DirectPlayback | ProviderCapabilities.Schedule | ProviderCapabilities.Poster | ProviderCapabilities.QualitySelection;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, JsonElement Data)> cache = new();
    static string S(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
    static int N(JsonElement e, string key) => int.TryParse(S(e, key), out var n) ? n : 0;
    static JsonElement O(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : JsonSerializer.SerializeToElement(new { });
    static string Image(string path) => string.IsNullOrWhiteSpace(path) ? "" : new Uri(new Uri(BaseUrl), path).ToString();
    async Task<JsonElement> Get(string path, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(BaseUrl + "/api/v1" + path, ct);
            if (!response.IsSuccessStatusCode)
                throw new ProviderException($"AniLiberty недоступна (HTTP {(int)response.StatusCode}).");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.Clone();
        }
        catch (HttpRequestException)
        {
            throw new ProviderException("Не удалось подключиться к AniLiberty.");
        }
        catch (TaskCanceledException)when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("AniLiberty не ответила вовремя.");
        }
    }

    async Task<JsonElement> Load(string id, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z0-9_-]{1,150}$"))
            throw new ProviderException("Недопустимый ID AniLiberty.");
        if (cache.TryGetValue(id, out var c) && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(2))
            return c.Data;
        var data = await Get("/anime/releases/" + id, ct);
        cache[id] = (DateTimeOffset.UtcNow, data);
        return data;
    }

    public static ProviderRelease ParseRelease(JsonElement e)
    {
        var name = O(e, "name");
        var id = S(e, "id");
        var type = S(O(e, "type"), "value").ToLowerInvariant();
        if (type == "web")
            type = "ona";
        var shiki = N(O(e, "shikimori"), "id");
        var available = e.TryGetProperty("episodes", out var episodes) && episodes.ValueKind == JsonValueKind.Array ? episodes.EnumerateArray().Count(v => Qualities(v).Count > 0) : (int? )null;
        return new(id, string.Join(" / ", new[] { S(name, "main"), S(name, "english") }.Where(s => s != "").Distinct()), BaseUrl + "/anime/releases/release/" + S(e, "alias"), N(e, "year"), type, N(e, "episodes_total"), available, Image(S(O(e, "poster"), "src")), shiki > 0 ? new("shikimori", shiki.ToString()) : null);
    }

    static bool IsBlocked(JsonElement r) => new[]
    {
        "is_blocked_by_geo",
        "is_blocked_by_copyrights"
    }.Any(k => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True);
    static void EnsureAllowed(JsonElement r)
    {
        if (IsBlocked(r))
            throw new ProviderException("Релиз ограничен провайдером и недоступен для просмотра.");
    }

    public static IReadOnlyList<MediaVariant> Qualities(JsonElement e) => new[]
    {
        480,
        720,
        1080
    }.Select(q => new MediaVariant(q + "p", S(e, "hls_" + q))).Where(v => Uri.TryCreate(v.Location, UriKind.Absolute, out var u) && u.Scheme == "https").OrderByDescending(v => v.Height).ToArray();
    public async Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default)
    {
        try
        {
            await Get("/anime/releases/latest?limit=1", ct);
            return new(true, "API v1 доступен");
        }
        catch (IOException ex)
        {
            return new(false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<ProviderRelease>> Search(string query, CancellationToken ct = default)
    {
        var data = await Get(string.IsNullOrWhiteSpace(query) ? "/anime/catalog/releases?limit=50&page=1" : "/app/search/releases?query=" + Uri.EscapeDataString(query), ct);
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var rows))
            data = rows;
        return data.EnumerateArray().Select(ParseRelease).ToArray();
    }

    public async Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default) => ParseRelease(await Load(id, ct));
    public async Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default)
    {
        var r = await Load(id, ct);
        EnsureAllowed(r);
        if (!r.TryGetProperty("episodes", out var eps) || eps.ValueKind != JsonValueKind.Array)
            return[];
        return eps.EnumerateArray().Select(e => new ProviderEpisode(S(e, "id"), N(e, "ordinal"), S(e, "name") == "" ? $"Серия {N(e, "ordinal")}" : S(e, "name"), id, Available: Qualities(e).Count > 0)).OrderBy(e => e.Number).ToArray();
    }

    public async Task<PlaybackSource> GetPlayback(string releaseId, ProviderEpisode episode, CancellationToken ct = default)
    {
        var r = await Load(releaseId, ct);
        EnsureAllowed(r);
        var ep = r.GetProperty("episodes").EnumerateArray().FirstOrDefault(e => S(e, "id") == episode.Id);
        if (ep.ValueKind == JsonValueKind.Undefined)
            throw new ProviderException("Серия не принадлежит выбранному релизу.");
        var qualities = Qualities(ep);
        if (qualities.Count == 0)
            throw new ProviderException("Для серии нет доступного официального потока.");
        return new(PlaybackKind.DirectMedia, qualities[0].Location, Variants: qualities);
    }

    public static IReadOnlyList<ScheduleItem> ParseSchedule(JsonElement data)
    {
        var items = new List<ScheduleItem>();
        foreach (var e in data.EnumerateArray())
        {
            var raw = O(e, "release");
            var release = ParseRelease(raw);
            if (release.Id == "")
                continue;
            var published = O(e, "published_release_episode");
            var number = N(published, "ordinal");
            bool ready = !IsBlocked(raw) && number > 0 && Qualities(published).Count > 0;
            var shiki = release.MetadataIdentity is { } identity && long.TryParse(identity.Id, out var id) ? id : 0;
            var day = N(O(raw, "publish_day"), "value");
            items.Add(new(new Anime { ShikimoriId = shiki, Name = release.Title, Poster = release.Poster, Year = release.Year, Kind = release.Kind }, ready ? number : N(e, "next_release_episode_number"), null) { ProviderId = "anilibria", ProviderName = "AniLibria", Release = release, ProviderDay = day is >= 1 and <= 7 ? (DayOfWeek)(day % 7) : null, Released = ready, TimeHint = ready ? "Опубликована провайдером" : "Ожидается · время не указано", Availability = ready ? "✓ Серия доступна" : "Ожидается" });
        }

        return items;
    }

    public async Task<IReadOnlyList<ScheduleItem>> Schedule(CancellationToken ct = default) => ParseSchedule(await Get("/anime/schedule/week", ct));
}
