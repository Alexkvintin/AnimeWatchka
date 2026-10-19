using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnimeApp.Services.Database;
using AnimeApp.Services.Providers;

namespace AnimeApp.Services.Playback;

public sealed record DownloadPreferences(bool Automatic = true, string Folder = "")
{
    public string Root => Automatic ? Path.Combine(AppContext.BaseDirectory, "Downloads") :
        Path.IsPathFullyQualified(Folder) ? Path.GetFullPath(Folder) : throw new IOException("Выберите абсолютный путь папки загрузок в настройках.");
}
public sealed record DownloadIdentity(string ProviderId, string ReleaseId, string EpisodeId, long AnimeId = 0);
public sealed record SavedEpisode(string Title, string Media, int Episode = 1, DownloadIdentity? Identity = null);
public sealed record DownloadEntry(string Directory, SavedEpisode Data)
{
    public string Label => $"{Data.Title} · серия {Data.Episode}";
    public string File => DownloadedEpisodeProvider.Read(Directory).File;
}
public static class DownloadLibrary
{
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    private static string Name(string value) => new string(value.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).Take(50).ToArray()).Trim().TrimEnd('.');
    public static string ReleaseFolder(string root, string title, string voice, DownloadIdentity identity) =>
        Path.Combine(root, Name(title) + " - AW-" + Hash(identity.AnimeId > 0 ? identity.AnimeId.ToString() : identity.ProviderId + "\n" + identity.ReleaseId),
            Name(voice) + " - AW-" + Hash(identity.ProviderId + "\n" + identity.ReleaseId));
    public static IReadOnlyList<DownloadEntry> Read(string root)
    {
        var result = new List<DownloadEntry>();
        if (!System.IO.Directory.Exists(root)) return result;
        // Only inspect our three-level layout, never traverse links or arbitrary user folders.
        IEnumerable<string> Children(string path, string pattern) => System.IO.Directory.EnumerateDirectories(path, pattern)
            .Where(p => (System.IO.File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0);
        try
        {
            foreach (var title in Children(root, "* - AW-*"))
            foreach (var voice in Children(title, "* - AW-*"))
            foreach (var episode in Children(voice, "AnimeWatchka - *"))
            {
                try
                {
                    var metadata = Path.Combine(episode, "download.json");
                    if (!File.Exists(metadata) || new FileInfo(metadata).Length > 16384) continue;
                    var data = JsonSerializer.Deserialize<SavedEpisode>(File.ReadAllText(metadata));
                    if (data?.Identity is null) continue;
                    DownloadedEpisodeProvider.Read(episode);
                    result.Add(new(episode, data));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { }
        return result;
    }
    public static DownloadEntry? Find(IEnumerable<DownloadEntry> entries, string provider, string release, string episode) =>
        entries.FirstOrDefault(e => e.Data.Identity is { } id && id.ProviderId == provider && id.ReleaseId == release && id.EpisodeId == episode);
    public static async Task<PlaybackSource> Resolve(LibraryRepository repository, IPlaybackProvider provider, string release, ProviderEpisode episode, CancellationToken ct = default)
    {
        var settings = await repository.Setting<DownloadPreferences>("downloads", ct) ?? new();
        var entries = await Task.Run(() => Read(settings.Root), ct);
        var saved = Find(entries, provider.Id, release, episode.Id);
        if (saved is not null) return new(PlaybackKind.LocalFile, saved.File);
        return await provider.GetPlayback(release, episode, ct);
    }
}

