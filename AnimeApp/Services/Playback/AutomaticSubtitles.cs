using System.Net.Http.Json;
using System.Text.Json;

namespace AnimeApp.Services.Playback;

public record OnlineSubtitle(long Id, string Language, string Author, double Duration)
{
    public string Url => $"https://smotret-anime.app/translations/ass/{Id}?download=1";
    public override string ToString() => $"{(Language == "uk" ? "Українські" : "Русские")} · {Author} · Anime365";
}

/// <summary>Never infer a season from a similar name, or mistake an opening for episode one.</summary>
public sealed class AutomaticSubtitles(HttpClient http)
{
    public async Task<string> Download(OnlineSubtitle subtitle, CancellationToken ct = default)
    {
        if (subtitle.Id <= 0 || subtitle.Language is not ("ru" or "uk")) throw new IOException("Некорректные субтитры.");
        using var response = await http.GetAsync(subtitle.Url, HttpCompletionOption.ResponseHeadersRead, ct);
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
        if (!content.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase) || !content.Contains("[Events]", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Источник не вернул файл субтитров.");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeApp", "SubtitleCache");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{subtitle.Language}-Anime365-{subtitle.Id}.ass");
        await File.WriteAllBytesAsync(path, buffer.ToArray(), ct);
        return path;
    }
    private async Task<JsonElement> Get(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync("https://smotret-anime.app/api/" + path, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct)
            ?? throw new IOException("Пустой ответ каталога субтитров.");
        return doc.RootElement.GetProperty("data").Clone();
    }
    private static long Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && long.TryParse(v.ToString(), out var n) ? n : 0;
    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "";

    public async Task<IReadOnlyList<OnlineSubtitle>> Find(long animeId, string title, int episode, CancellationToken ct = default)
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
            Number(t, "isActive") == 1 && Text(t, "typeKind") == "sub" && Text(t, "typeLang") is "ru" or "uk" && Number(t, "id") > 0)
        .OrderBy(t => Text(t, "typeLang")).ThenByDescending(t => Number(t, "priority"))
        .Select(t => new OnlineSubtitle(Number(t, "id"), Text(t, "typeLang"), Text(t, "authorsSummary"),
            t.TryGetProperty("duration", out var d) && d.TryGetDouble(out var seconds) ? seconds : 0))
        .DistinctBy(t => t.Id).ToArray();
}
