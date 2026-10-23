using System.Net.Http.Json;
using System.Text.Json;

namespace AnimeApp.Services.Playback;

public record OnlineSubtitle(long Id, string Language, string Author, double Duration, string Service = "Anime365", string Format = "ass")
{
    public string Url => $"https://smotret-anime.app/translations/ass/{Id}?download=1";
    public override string ToString() => $"{(Language switch { "uk" => "Українські", "en" => "English", _ => "Русские" })} · {Author} · {Service}";
}

/// <summary>Never infer a season from a similar name, or mistake an opening for episode one.</summary>
public sealed class AutomaticSubtitles(HttpClient http, OpenSubtitlesService? openSubtitles = null, AnimeApp.Services.Providers.IMetadataProvider? metadata = null)
{
    public async Task<string> Download(OnlineSubtitle subtitle, CancellationToken ct = default)
    {
        if (subtitle.Id <= 0 || subtitle.Language is not ("ru" or "uk" or "en")) throw new IOException("Некорректные субтитры.");
        if(subtitle.Service is not ("Anime365" or "OpenSubtitles") || subtitle.Format is not ("ass" or "srt")) throw new IOException("Неизвестный формат перевода.");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeApp", "SubtitleCache");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{subtitle.Language}-{subtitle.Service}-{subtitle.Id}.{subtitle.Format}");
        if(File.Exists(path) && new FileInfo(path).Length is > 0 and <= 4194304) return path;
        var url=subtitle.Service=="OpenSubtitles" ? await (openSubtitles ?? throw new IOException("OpenSubtitles не настроен.")).DownloadUrl(subtitle.Id,ct) : subtitle.Url;
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new IOException("Файл субтитров слишком большой.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > 4 * 1024 * 1024) throw new IOException("Файл субтитров слишком большой.");
            buffer.Write(chunk, 0, count);
        }
        var content = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        if (subtitle.Format=="ass" ? (!content.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase) || !content.Contains("[Events]", StringComparison.OrdinalIgnoreCase)) : !System.Text.RegularExpressions.Regex.IsMatch(content, @"\d{2}:\d{2}:\d{2}[,.]\d{3}\s*-->\s*\d{2}:\d{2}:\d{2}[,.]\d{3}"))
            throw new IOException("Источник не вернул файл субтитров.");
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { await File.WriteAllBytesAsync(temporary, buffer.ToArray(), ct); File.Move(temporary,path,true); }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, JsonElement Data)> cache = new();
    private async Task<JsonElement> Get(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (cache.TryGetValue(path, out var saved) && DateTimeOffset.UtcNow - saved.At < TimeSpan.FromMinutes(10))
            return saved.Data;
        using var response = await http.GetAsync("https://smotret-anime.app/api/" + path, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct)
            ?? throw new IOException("Пустой ответ каталога субтитров.");
        if(!doc.RootElement.TryGetProperty("data",out var payload) || payload.ValueKind!=JsonValueKind.Array)
            throw new IOException("Каталог субтитров вернул неподдерживаемый ответ.");
        var data = payload.Clone();
        ct.ThrowIfCancellationRequested();
        if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            if (cache.Count >= 200) cache.Clear();
            cache[path] = (DateTimeOffset.UtcNow, data);
        }
        return data;
    }
    private static long Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && long.TryParse(v.ToString(), out var n) ? n : 0;
    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, AnimeApp.Models.Anime> identities = new();
    public async Task<IReadOnlyList<OnlineSubtitle>> Find(long animeId, string title, int episode, CancellationToken ct = default) {
        if(animeId<=0 || episode<=0 || string.IsNullOrWhiteSpace(title)) return [];
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        async Task<IReadOnlyList<OnlineSubtitle>> OpenSubtitles() {
            if(openSubtitles is null || metadata is null || !await openSubtitles.Configured(deadline.Token)) return [];
            if(!identities.TryGetValue(animeId,out var anime)) {
                anime=await metadata.Details(animeId,deadline.Token);
                if(identities.Count>100) identities.Clear(); identities[animeId]=anime;
            }
            return await openSubtitles.Find(new[]{anime.EnglishName,anime.Name,anime.RussianName}.Where(n=>!string.IsNullOrWhiteSpace(n)).ToArray(),anime.Year??0,episode,anime.Kind,deadline.Token);
        }
        async Task<IReadOnlyList<OnlineSubtitle>> Safe(Func<Task<IReadOnlyList<OnlineSubtitle>>> find) {
            try { return await find(); }
            catch(Exception ex) when(ex is HttpRequestException or IOException or JsonException or InvalidOperationException or TaskCanceledException) { ct.ThrowIfCancellationRequested(); return []; }
        }
        var batches=await Task.WhenAll(Safe(()=>FindAnime365(animeId,title,episode,deadline.Token)),Safe(OpenSubtitles));
        return batches.SelectMany(b=>b).ToArray();
    }
    private async Task<IReadOnlyList<OnlineSubtitle>> FindAnime365(long animeId, string title, int episode, CancellationToken ct)
    {
        if (animeId <= 0 || episode <= 0 || string.IsNullOrWhiteSpace(title)) return [];
        var series = await Get($"series?query={Uri.EscapeDataString(title)}&limit=100", ct);
        var matches = series.EnumerateArray().Where(s => Number(s, "myAnimeListId") == animeId).ToArray();
        if (matches.Length != 1) return [];
        var seriesId = Number(matches[0], "id");
        var episodes = await Get($"episodes?seriesId={seriesId}&episodeInt={episode}&isActive=1&limit=100", ct);
        var candidates = episodes.EnumerateArray().Where(e => Number(e, "episodeInt") == episode &&
            Text(e, "episodeType") == Text(matches[0], "type")).ToArray();
        if (candidates.Length != 1) return [];
        var episodeId = Number(candidates[0], "id");
        var translations = await Get($"translations?episodeId={episodeId}&isActive=1&limit=100", ct);
        return Parse(translations, seriesId, episodeId);
    }

    public static IReadOnlyList<OnlineSubtitle> Parse(JsonElement data, long seriesId, long episodeId) =>
        data.EnumerateArray().Where(t => Number(t, "seriesId") == seriesId && Number(t, "episodeId") == episodeId &&
            Number(t, "isActive") == 1 && Text(t, "typeKind") == "sub" && Text(t, "typeLang") is "ru" or "uk" or "en" && Number(t, "id") > 0)
        .OrderBy(t => Text(t, "typeLang") switch { "ru" => 0, "uk" => 1, _ => 2 }).ThenByDescending(t => Number(t, "priority"))
        .Select(t => new OnlineSubtitle(Number(t, "id"), Text(t, "typeLang"), Text(t, "authorsSummary"),
            t.TryGetProperty("duration", out var d) && d.TryGetDouble(out var seconds) ? seconds : 0))
        .DistinctBy(t => t.Id).ToArray();
}
