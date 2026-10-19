using System.Text.Json;
using AnimeApp.Services.Providers;
namespace AnimeApp.Services.Playback;
// Offline playback is isolated from network providers and never marks a Shikimori title.
public sealed class DownloadedEpisodeProvider : IAnimeProvider
{
    public string Id => "downloads";
    public string DisplayName => "Скачанное";
    public string Language => "multi";
    private sealed record Metadata(string Title, string Media, int Episode = 1);
    public static (string Title, string File, int Episode) Read(string directory)
    {
        if (Path.GetFileName(directory).StartsWith(EpisodeDownloader.PendingName) || File.Exists(Path.Combine(directory, "download.pending"))) throw new InvalidDataException();
        var file = Path.Combine(directory, "download.json");
        if (new FileInfo(file).Length > 16384) throw new InvalidDataException();
        var data = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(file)) ?? throw new InvalidDataException();
        if (data.Media is not ("episode.mp4" or "episode.mkv" or "episode.webm" or "episode.m3u8")) throw new InvalidDataException();
        var media = Path.Combine(directory, data.Media);
        if (!File.Exists(media)) throw new FileNotFoundException();
        if (data.Media == "episode.m3u8")
        {
            if (new FileInfo(media).Length > 2_000_000) throw new InvalidDataException();
            foreach (var line in File.ReadLines(media).Where(l => l.Length > 0))
            {
                string? local = !line.StartsWith('#') ? line : line.StartsWith("#EXT-X-MAP:") ? System.Text.RegularExpressions.Regex.Match(line, "^#EXT-X-MAP:URI=\"([^\"]+)\"$").Groups[1].Value : null;
                if (local is not null && (local != Path.GetFileName(local) || local.Contains(':') || local.Contains('\\') || !File.Exists(Path.Combine(directory, local)))) throw new InvalidDataException();
                if (line.StartsWith('#') && (line.Contains("URI=") && !line.StartsWith("#EXT-X-MAP:") || line.StartsWith("#EXT-X-STREAM-INF") || line.StartsWith("#EXT-X-KEY"))) throw new InvalidDataException();
            }
        }
        return (data.Title, media, Math.Clamp(data.Episode, 0, 99999));
    }
    public Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default) => Task.FromResult(new ProviderAvailability(true, ""));
    public Task<IReadOnlyList<ProviderRelease>> Search(string q, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderRelease>>([]);
    public Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default) => Task.FromResult(new ProviderRelease(id, Read(id).Title));
    public Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderEpisode>>([new(Read(id).File, Read(id).Episode, Read(id).Title, CountsTowardProgress: false)]);
    public Task<PlaybackSource> GetPlayback(string id, ProviderEpisode episode, CancellationToken ct = default) => Task.FromResult(new PlaybackSource(PlaybackKind.LocalFile, Read(id).File));
}
