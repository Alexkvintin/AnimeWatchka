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
            using var originalContent = content;
            var body = content is null ? null : await content.ReadAsByteArrayAsync(ct);
            for (var attempt = 0; ; attempt++)
            {
            using var req = new HttpRequestMessage(content is null ? HttpMethod.Get : HttpMethod.Post, url)
            {
                Content = body is null ? null : new ByteArrayContent(body)
            };
            if (content is not null)
                foreach (var header in content.Headers) req.Content!.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (attempt > 0) req.Headers.CacheControl = new() { NoCache = true };
            using var r = await http.SendAsync(req, ct);
            var text = await r.Content.ReadAsStringAsync(ct);
            var databaseError = System.Text.RegularExpressions.Regex.IsMatch(text, @"<title>\s*MySQL Fatal Error\s*</title>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            // Retry read-only catalog/player requests once; never cache an outage page.
            if (attempt == 0 && (databaseError || r.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
            {
                await Task.Delay(500, ct);
                continue;
            }
            if (r.StatusCode == HttpStatusCode.NotFound)
                throw new ProviderException("Релиз или серия удалены. Смените релиз или удалите сохранённую связь.");
            if (!r.IsSuccessStatusCode)
                throw new ProviderException($"AnimeVost сейчас недоступен (HTTP {(int)r.StatusCode}).");
            if (string.IsNullOrWhiteSpace(text))
                throw new ProviderException("AnimeVost вернул пустой ответ. Попробуйте позже.");
            // The site can return its database error page with HTTP 200.
            // Do not treat that response as a release or an empty search result.
            if (databaseError)
                throw new ProviderException("На стороне AnimeVost произошёл сбой базы данных. Сайт временно не отдаёт серии. Попробуйте позже или выберите другую озвучку.");
            return text;
            }
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
                expanded = await AvailableVariants(expanded, page, ct);
                return new(PlaybackKind.DirectMedia, QualityNegotiation.Select(expanded, "Auto")!.Location, new Dictionary<string, string> { { "Referer", page.ToString() } }, expanded);
            }

            var frame = parser.Frame(html, page);
            if (frame is null)
                return new(PlaybackKind.WebPage, page.ToString());
            page = new Uri(frame);
        }

        throw new ProviderException("Не удалось получить адрес видео: структура плеера изменилась.");
    }

    private async Task<IReadOnlyList<MediaVariant>> AvailableVariants(IReadOnlyList<MediaVariant> variants, Uri page, CancellationToken ct)
    {
        var remaining = variants.OrderByDescending(v => v.Height).ToList();
        foreach (var variant in remaining.ToArray())
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, variant.Location);
                request.Headers.Referrer = page;
                request.Headers.Range = new(0, 1023);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                // Older releases can advertise a removed HD file alongside a working SD file.
                // Only discard explicitly missing files; transient failures must not hide quality choices.
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                { remaining.Remove(variant); continue; }
                return remaining;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return remaining; }
            catch (HttpRequestException) { return remaining; }
        }
        throw new ProviderException("Видео этой серии удалено с серверов AnimeVost во всех доступных качествах.");
    }
}
