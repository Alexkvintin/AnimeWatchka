using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AnimeApp.Services.Playback;
using System.Text.Json;

namespace AnimeApp.Services.Providers;
public record TemplateDefinition(string Id, string Name, string BaseUrl, string SearchTemplate, string ReleaseSelector, string EpisodeSelector, string PosterSelector, string VideoSelector, string Language = "unknown")
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 120)
            throw new ArgumentException("Укажите название адаптера (до 120 символов).");
        if (!System.Text.RegularExpressions.Regex.IsMatch(Id, @"^template-[a-f0-9]{32}$"))
            throw new ArgumentException("Недопустимый идентификатор адаптера.");
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var root) || root.Scheme != "https" || root.UserInfo != "")
            throw new ArgumentException("Нужен HTTPS-адрес сайта.");
        if (!SearchTemplate.Contains("{query}") || !Uri.TryCreate(SearchTemplate.Replace("{query}", "test"), UriKind.Absolute, out var search) || search.Scheme != "https" || search.Host != root.Host || search.UserInfo != "")
            throw new ArgumentException("Шаблон поиска должен содержать {query} и вести на тот же HTTPS-сайт.");
        var doc = new HtmlParser().ParseDocument("");
        foreach (var selector in new[]
        {
            ReleaseSelector,
            EpisodeSelector,
            PosterSelector,
            VideoSelector
        }

        )
        {
            if (string.IsNullOrWhiteSpace(selector) || selector.Length > 300)
                throw new ArgumentException("Заполните CSS-селекторы (до 300 символов).");
            try
            {
                doc.QuerySelector(selector);
            }
            catch (Exception)
            {
                throw new ArgumentException("Один из CSS-селекторов имеет неверный формат.");
            }
        }
    }
}

// Data-only adapter: no scripts, iframe extraction, authentication or protection workarounds.
public sealed class TemplateProvider(TemplateDefinition definition, HttpClient http) : IAnimeProvider
{
    public string Id => definition.Id;
    public string DisplayName => definition.Name;
    public string Language => definition.Language;
    public string Url => definition.BaseUrl;
    public ProviderCapabilities Features => ProviderCapabilities.Search | ProviderCapabilities.Episodes | ProviderCapabilities.Poster | ProviderCapabilities.DirectPlayback;

    string Address(string value, string relativeTo, bool sameHost)
    {
        if (!Uri.TryCreate(new Uri(relativeTo), value, out var uri) || uri.Scheme != "https" || uri.UserInfo != "" || sameHost && uri.Host != new Uri(Url).Host)
            throw new ProviderException("Ссылка не принадлежит разрешённому HTTPS-источнику.");
        return uri.ToString();
    }

    async Task<IDocument> Page(string url, CancellationToken ct)
    {
        definition.Validate();
        url = Address(url, Url, true);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException($"Страница недоступна (HTTP {(int)response.StatusCode}).");
        Address(response.RequestMessage?.RequestUri?.ToString() ?? url, Url, true);
        if (response.Content.Headers.ContentType?.MediaType is not ("text/html" or "application/xhtml+xml"))
            throw new ProviderException("Адаптер ожидает обычную HTML-страницу.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[1_000_001];
        var count = await reader.ReadBlockAsync(buffer, ct);
        if (count > 1_000_000)
            throw new ProviderException("Страница слишком велика для шаблонного адаптера.");
        return await new HtmlParser().ParseDocumentAsync(new string (buffer, 0, count), ct);
    }

    public async Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default)
    {
        try
        {
            await Page(Url, ct);
            return new(true, "HTML доступен; селекторы требуют проверки на релизе.");
        }
        catch (IOException)
        {
            return new(false, "Страница недоступна.");
        }
    }

    public async Task<IReadOnlyList<ProviderRelease>> Search(string query, CancellationToken ct = default)
    {
        var url = definition.SearchTemplate.Replace("{query}", Uri.EscapeDataString(query));
        var doc = await Page(url, ct);
        var result = new List<ProviderRelease>();
        foreach (var a in doc.QuerySelectorAll(definition.ReleaseSelector).Take(100))
        {
            var href = a.GetAttribute("href");
            if (href is null)
                continue;
            try
            {
                var id = Address(href, url, true);
                result.Add(new(id, a.TextContent.Trim(), id));
            }
            catch (ProviderException)
            {
            }
        }

        return result.DistinctBy(r => r.Id).ToArray();
    }

    public async Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default)
    {
        var doc = await Page(id, ct);
        var poster = doc.QuerySelector(definition.PosterSelector);
        var raw = poster?.GetAttribute("src") ?? poster?.GetAttribute("content");
        return new(id, doc.QuerySelector("h1")?.TextContent.Trim() ?? doc.Title ?? DisplayName, id, Poster: raw is null ? "" : Address(raw, id, false));
    }

    public async Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default)
    {
        var doc = await Page(id, ct);
        var episodes = new List<ProviderEpisode>();
        foreach (var a in doc.QuerySelectorAll(definition.EpisodeSelector).Take(500))
        {
            var href = a.GetAttribute("href");
            if (href is null)
                continue;
            try
            {
                var page = Address(href, id, true);
                episodes.Add(new(page, episodes.Count + 1, a.TextContent.Trim(), id, page, CountsTowardProgress: false));
            }
            catch (ProviderException)
            {
            }
        }

        return episodes.DistinctBy(e => e.Id).ToArray();
    }

    public async Task<PlaybackSource> GetPlayback(string releaseId, ProviderEpisode episode, CancellationToken ct = default)
    {
        if (episode.ReleaseId != releaseId)
            throw new ProviderException("Серия не принадлежит релизу.");
        var doc = await Page(episode.PageUrl, ct);
        var element = doc.QuerySelector(definition.VideoSelector);
        if (element?.LocalName is not ("video" or "source"))
            throw new ProviderException("Селектор должен указывать на обычный HTML5 video/source.");
        var src = element.GetAttribute("src");
        if (string.IsNullOrWhiteSpace(src))
            throw new ProviderException("Прямое HTML5-видео не найдено; защищённые плееры не извлекаются.");
        return new(PlaybackKind.DirectMedia, Address(src, episode.PageUrl, false));
    }
}

public static class TemplateStore
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeApp", "adapters");

    public static async Task Save(TemplateDefinition definition)
    {
        definition.Validate();
        Directory.CreateDirectory(DirectoryPath);
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, definition.Id + ".json"), JsonSerializer.Serialize(definition, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static IEnumerable<TemplateDefinition> Load()
    {
        if (!Directory.Exists(DirectoryPath))
            yield break;
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "template-*.json"))
        {
            TemplateDefinition? d = null;
            try
            {
                d = JsonSerializer.Deserialize<TemplateDefinition>(File.ReadAllText(file));
                d?.Validate();
            }
            catch (Exception ex)when (ex is IOException or JsonException or ArgumentException)
            {
                d = null;
            }

            if (d is not null)
                yield return d;
        }
    }
}
