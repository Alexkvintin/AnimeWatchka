using System.Globalization;
using System.Text.Json;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Providers;

namespace AnimeApp.Providers.Mikai;

public sealed class SubtitlesProvider(MikaiClient client) : IAnimeProvider
{
    public string Id => "subtitles";
    public string DisplayName => "Субтитры";
    public string Language => "ja";
    public string Url => "https://mikai.me";
    public ProviderCapabilities Features => ProviderCapabilities.Search | ProviderCapabilities.Episodes | ProviderCapabilities.DirectPlayback | ProviderCapabilities.QualitySelection;
    public string Capabilities => "Субтитры UK · каталог Mikai (https://mikai.me) · вшитый текст зависит от релиза";
    private static string Text(JsonElement e, params string[] path) => MikaiProvider.Text(e, path);
    private static string Key(string id) => long.TryParse(id, out var n) && n > 0 ? id : throw new ProviderException("Некорректный релиз субтитров.");
    public async Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default)
    {
        try { await client.Get("health", ct); return new(true, "Каталог доступен · субтитры UK"); }
        catch (IOException e) { return new(false, e.Message); }
    }
    public async Task<IReadOnlyList<ProviderRelease>> Search(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var data = await client.Get($"anime?search={Uri.EscapeDataString(query.Trim())}&limit=30", ct);
        return data.GetProperty("result").EnumerateArray().Select(MikaiProvider.ParseRelease).ToArray();
    }
    public async Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default) =>
        MikaiProvider.ParseRelease((await client.Get("anime/" + Key(id), ct)).GetProperty("result"));

    public static IReadOnlyList<ProviderEpisode> ParseEpisodes(string id, JsonElement data, bool allTeams = false)
    {
        if (data.TryGetProperty("licensed", out var licensed) && licensed.ValueKind == JsonValueKind.True) return [];
        var groups = new List<(string Id, List<ProviderEpisode> Episodes)>();
        foreach (var release in data.GetProperty("releases").EnumerateArray().Where(r => Text(r, "kind") == "sub"))
        {
            var teamId = Text(release, "id");
            if (teamId.Length == 0) continue;
            var team = string.Join(", ", release.GetProperty("teams").EnumerateArray().Select(t => Text(t, "name")));
            var episodes = new List<ProviderEpisode>();
            foreach (var episode in release.GetProperty("episodes").EnumerateArray())
            {
                if (!decimal.TryParse(Text(episode, "number"), CultureInfo.InvariantCulture, out var n) || n <= 0 || n > int.MaxValue) continue;
                var url = episode.GetProperty("sources").EnumerateArray().Select(s => Text(s, "embedUrl"))
                    .FirstOrDefault(s => Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host == "ashdi.vip");
                if (url is null) continue;
                episodes.Add(new($"{id}:{teamId}:{n.ToString(CultureInfo.InvariantCulture)}", (int)n,
                    $"Серия {Text(episode, "label")} · UK · {team} / Mikai", id, url, CountsTowardProgress: n == decimal.Truncate(n)));
            }
            groups.Add((teamId, episodes.DistinctBy(e => e.Id).OrderBy(e => e.Number).ToList()));
        }
        // One translation for a season. Persist the team in the episode identity so an
        // existing download/history cannot silently become a different translation.
        if (allTeams) return groups.SelectMany(g => g.Episodes).ToArray();
        return groups.OrderByDescending(g => g.Episodes.Count).ThenBy(g => g.Id, StringComparer.Ordinal)
            .Select(g => (IReadOnlyList<ProviderEpisode>)g.Episodes).FirstOrDefault() ?? [];
    }
    public async Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default) =>
        ParseEpisodes(Key(id), (await client.Get("anime/" + Key(id) + "/player", ct)).GetProperty("result"));
    public async Task<PlaybackSource> GetPlayback(string releaseId, ProviderEpisode episode, CancellationToken ct = default)
    {
        var data = (await client.Get("anime/" + Key(releaseId) + "/player", ct)).GetProperty("result");
        var current = ParseEpisodes(releaseId, data, allTeams: true).FirstOrDefault(e => e.Id == episode.Id)
            ?? throw new ProviderException("Этот перевод серии больше недоступен.");
        return await client.ResolveVideo(current.PageUrl, ct);
    }
}
