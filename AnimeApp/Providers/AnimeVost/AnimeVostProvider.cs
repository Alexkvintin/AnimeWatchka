using System.Net;
using System.Collections.Concurrent;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnimeApp.Providers.AnimeVost;
public sealed partial class AnimeVostProvider(HttpClient http, IAnimeVostParser parser, ILogger<AnimeVostProvider>? logger = null) : IAnimeProvider, IScheduleProvider
{
    private readonly ILogger log = logger ?? NullLogger<AnimeVostProvider>.Instance;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, AnimeVostRelease Release)> cache = new();
    public string Id => "animevost";
    public string DisplayName => "AnimeVost";
    public string Language => "ru";
    public string Url => "https://animevost.org";
    public ProviderCapabilities Features => ProviderCapabilities.Search | ProviderCapabilities.Episodes | ProviderCapabilities.DirectPlayback | ProviderCapabilities.WebPlayback | ProviderCapabilities.Poster | ProviderCapabilities.Schedule | ProviderCapabilities.QualitySelection;

    private static Uri Validate(string id)
    {
        if (!Uri.TryCreate(id, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host != "animevost.org")
            throw new ProviderException("Недопустимый адрес релиза AnimeVost.");
        return u;
    }

    private async Task<string> Fetch(string url, CancellationToken ct, HttpContent? content = null)
    {
        try
        {
            using var req = new HttpRequestMessage(content is null ? HttpMethod.Get : HttpMethod.Post, url)
            {
                Content = content
            };
            using var r = await http.SendAsync(req, ct);
            if (r.StatusCode == HttpStatusCode.NotFound)
                throw new ProviderException("Релиз или серия удалены. Смените релиз или удалите сохранённую связь.");
            if (!r.IsSuccessStatusCode)
                throw new ProviderException($"AnimeVost сейчас недоступен (HTTP {(int)r.StatusCode}).");
            var text = await r.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text))
                throw new ProviderException("AnimeVost вернул пустой ответ. Попробуйте позже.");
            return text;
        }
        catch (TaskCanceledException)when (!ct.IsCancellationRequested)
        {
            log.LogWarning("AnimeVost timeout");
            throw new ProviderException("AnimeVost не ответил вовремя. Попробуйте позже.");
        }
        catch (HttpRequestException)
        {
            log.LogWarning("AnimeVost connection failed");
            throw new ProviderException("AnimeVost сейчас недоступен. Проверьте соединение с интернетом.");
        }
        catch (ProviderException ex)
        {
            log.LogWarning("AnimeVost: {Reason}", ex.Message);
            throw;
        }
    }

    public async Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default)
    {
        try
        {
            await Fetch("https://animevost.org/", ct);
            return new(true, "Сайт доступен");
        }
        catch (ProviderException ex)
        {
            return new(false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<ProviderRelease>> Search(string query, CancellationToken ct = default)
    {
        var html = await Fetch("https://animevost.org/index.php?do=search", ct, new FormUrlEncodedContent(new Dictionary<string, string> { { "do", "search" }, { "subaction", "search" }, { "story", query } }));
        return parser.SearchResults(html).Select(x => new ProviderRelease(x.Id, x.Title, x.Url, x.Year, x.Kind, x.Total)).ToList();
    }

    private async Task<AnimeVostRelease> Load(string id, CancellationToken ct)
    {
        var uri = Validate(id);
        if (cache.TryGetValue(id, out var c) && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(2))
            return c.Release;
        var html = await Fetch(uri.ToString(), ct);
        AnimeVostRelease r;
        try
        {
            r = parser.Release(html, id);
        }
        catch (ProviderException ex)
        {
            log.LogWarning("AnimeVost parser: {Reason}", ex.Message);
            throw;
        }

        cache[id] = (DateTimeOffset.UtcNow, r);
        return r;
    }

    public async Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default) => (await Load(id, ct)).Release;
    public async Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default) => (await Load(id, ct)).Episodes;
    public async Task<PlaybackSource> GetPlayback(string releaseId, ProviderEpisode episode, CancellationToken ct = default)
    {
        var release = await Load(releaseId, ct);
        if (!release.Episodes.Any(e => e.Id == episode.Id))
            throw new ProviderException("Эта серия отсутствует у выбранного релиза.");
        var page = new Uri(new Uri(releaseId), release.FrameEndpoint + Uri.EscapeDataString(episode.Id));
        // Follow only the public, same-origin iframe chain used by the site's own player.
        for (int depth = 0; depth < 3; depth++)
        {
            var html = await Fetch(page.ToString(), ct);
            var variants = parser.Media(html, page);
            var legacy = parser.LegacyFrame(html, page);
            if (legacy is not null)
            {
                try
                {
                    var legacyPage = new Uri(legacy);
                    var legacyVariants = parser.Media(await Fetch(legacy, ct), legacyPage);
                    if (legacyVariants.Count > 0)
                    {
                        variants = legacyVariants.Concat(variants).OrderByDescending(v => v.Height).DistinctBy(v => v.Location).ToArray();
                        page = legacyPage;
                    }
                }
                catch (ProviderException)when (variants.Count > 0)
                {
                // Keep the ordinary player usable if its published fallback is unavailable.
                }
            }

            if (variants.Count > 0)
            {
                var expanded = await QualityNegotiation.ExpandHls(http, variants, ct);
                return new(PlaybackKind.DirectMedia, QualityNegotiation.Select(expanded, "Auto")!.Location, new Dictionary<string, string> { { "Referer", page.ToString() } }, expanded);
            }

            var frame = parser.Frame(html, page);
            if (frame is null)
                return new(PlaybackKind.WebPage, page.ToString());
            page = new Uri(frame);
        }

        throw new ProviderException("Не удалось получить адрес видео: структура плеера изменилась.");
    }
}
