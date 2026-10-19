using AnimeApp.Models;
using AnimeApp.Services.Database;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Playback;

namespace AnimeApp.Providers;
public sealed class LocalFilesProvider(LibraryRepository repository) : IAnimeProvider
{
    public string Id => "local";
    public string DisplayName => "Local Files";
    public string Language => "multi";

    private async Task<string> Root(CancellationToken ct) => (await repository.Setting<AppSettings>("playback", ct))?.LocalFolder ?? "";
    private async Task<string> Validate(string id, CancellationToken ct)
    {
        var root = Path.GetFullPath(await Root(ct));
        var path = Path.GetFullPath(id);
        if (path.Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith(EpisodeDownloader.PendingName, StringComparison.Ordinal))) throw new InvalidOperationException("Загрузка ещё не завершена.");
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !path.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Файл вне папки библиотеки.");
        return path;
    }

    public async Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default) => new(Directory.Exists(await Root(ct)), "Укажите папку с локальными релизами в настройках.");
    public async Task<IReadOnlyList<ProviderRelease>> Search(string query, CancellationToken ct = default)
    {
        var root = await Root(ct);
        return await Task.Run<IReadOnlyList<ProviderRelease>>(() => !Directory.Exists(root) ? [] : Directory.EnumerateDirectories(root).Prepend(root).Where(x => !Path.GetFileName(x).StartsWith(EpisodeDownloader.PendingName)).Where(x => Path.GetFileName(x).Contains(query, StringComparison.OrdinalIgnoreCase)).Select(x => new ProviderRelease(x, Path.GetFileName(x))).ToList(), ct);
    }

    public async Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default) => new(await Validate(id, ct), Path.GetFileName(id));
    public async Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default)
    {
        var path = await Validate(id, ct);
        return await Task.Run<IReadOnlyList<ProviderEpisode>>(() => Directory.EnumerateFiles(path).Where(x => new[] { ".mp4", ".mkv", ".webm", ".avi" }.Contains(Path.GetExtension(x).ToLowerInvariant())).OrderBy(x => x, StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, System.Globalization.CompareOptions.NumericOrdering)).Select((x, i) => new ProviderEpisode(x, i + 1, Path.GetFileNameWithoutExtension(x))).ToList(), ct);
    }

    public async Task<PlaybackSource> GetPlayback(string releaseId, ProviderEpisode episode, CancellationToken ct = default)
    {
        var path = await Validate(episode.Id, ct);
        if (!File.Exists(path))
            throw new FileNotFoundException("Видео не найдено.");
        if (!new[]
        {
            ".mp4",
            ".mkv",
            ".webm",
            ".avi"
        }.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new InvalidOperationException("Неподдерживаемый тип видео.");
        return new(PlaybackKind.LocalFile, path);
    }
}
