using AnimeApp.Services.Playback;

namespace AnimeApp.Services.Providers;
public enum SourceSupport
{
    Known,
    Partial,
    Web,
    AdapterRequired,
    Unavailable
}

public record SourceProbeResult(SourceSupport Support, string Url, string Title, string Message, string ProviderId = "", string Poster = "", bool HtmlVideo = false, bool HtmlAudio = false, bool JsonLd = false);
public static class KnownSourceAdapters
{
    public static string Find(string host) => host.ToLowerInvariant() switch
    {
        "animevost.org" or "www.animevost.org" => "animevost",
        "aniliberty.top" or "www.aniliberty.top" or "anilibria.tv" or "api.anilibria.app" => "anilibria",
        "uakino.me" or "uakino.best" => "uakino",
        _ => ""
    };
}

public sealed class SourceProbeService(HttpClient http)
{
    public async Task<SourceProbeResult> Probe(string address, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            return new(SourceSupport.AdapterRequired, address, "", "Введите HTTPS адрес без логина и пароля.");
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new(SourceSupport.Unavailable, address, uri.Host, "Сайт недоступен или требует входа. Защита сайта не обходится.");
            if (response.RequestMessage?.RequestUri is Uri finalUri && finalUri.Scheme != "https")
                return new(SourceSupport.AdapterRequired, address, uri.Host, "Сайт перенаправляет на незащищённый адрес.");
            var known = KnownSourceAdapters.Find(uri.Host);
            if (known is "animevost" or "anilibria")
                return new(SourceSupport.Known, address, known == "animevost" ? "AnimeVost" : "AniLibria / AniLiberty", "Полная поддержка: встроенный адаптер · поиск · серии · прямое видео · расписание", known);
            if (response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml")
            {
                if (response.Content.Headers.ContentLength is> 2_000_000)
                    return new(SourceSupport.Web, address, uri.Host, "Страница доступна; подробная проверка пропущена из-за размера.", known);
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);
                var buffer = new char[1_000_001];
                var count = await reader.ReadBlockAsync(buffer, ct);
                if (count > 1_000_000)
                    return new(SourceSupport.Web, address, uri.Host, "Страница доступна; подробная проверка пропущена из-за размера.", known);
                var doc = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(new string (buffer, 0, count), ct);
                var title = doc.QuerySelector("meta[property='og:title']")?.GetAttribute("content") ?? doc.Title ?? uri.Host;
                var poster = doc.QuerySelector("meta[property='og:image']")?.GetAttribute("content") ?? "";
                bool video = doc.QuerySelector("video[src], video source[src]")is not null, audio = doc.QuerySelector("audio[src], audio source[src]")is not null, ld = doc.QuerySelector("script[type='application/ld+json']")is not null;
                return new(video || audio ? SourceSupport.Partial : SourceSupport.Web, address, title, video || audio ? "Частичная поддержка: найдено HTML5-медиа. Веб-просмотр доступен; каталог и серии требуют адаптера." : "Веб-просмотр: страница доступна, поиск и серии не подтверждены. Защищённые плееры не извлекаются.", known, poster, video, audio, ld);
            }

            return new(SourceSupport.AdapterRequired, address, uri.Host, "Сайт доступен, но для этого формата нужен отдельный адаптер.");
        }
        catch (Exception ex)when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(SourceSupport.Unavailable, address, uri.Host, "Не удалось подключиться к сайту.");
        }
    }
}

public record WebSourceDefinition(string Id, string Title, string Url, string Language = "unknown");
public sealed class GenericWebProvider(WebSourceDefinition definition, HttpClient http) : IAnimeProvider
{
    public string Id => definition.Id;
    public string DisplayName => definition.Title;
    public string Language => definition.Language;
    public string Url => definition.Url;
    public ProviderCapabilities Features => ProviderCapabilities.WebPlayback;
    public string Capabilities => "Веб-страница · без поиска и списка серий";

    public async Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default)
    {
        var p = await new SourceProbeService(http).Probe(Url, ct);
        return new(p.Support is SourceSupport.Web or SourceSupport.Partial, p.Message);
    }

    public Task<IReadOnlyList<ProviderRelease>> Search(string query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderRelease>>([]);
    public Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default) => throw new ProviderException("Веб-источник не предоставляет список релизов. Откройте сохранённую страницу в настройках.");
    public Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderEpisode>>([]);
    public Task<PlaybackSource> GetPlayback(string releaseId, ProviderEpisode episode, CancellationToken ct = default) => Task.FromResult(new PlaybackSource(PlaybackKind.WebPage, Url));
}
