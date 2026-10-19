using AnimeApp.Services.Providers;
namespace AnimeApp.Services.Playback;
public sealed record SeasonProgress(int Done, int Total, int Episode, DownloadProgress? Transfer = null);
public sealed record SeasonResult(int Downloaded, int Skipped, IReadOnlyList<int> Failed);
public sealed class SeasonDownloader(HttpClient http)
{
    public async Task<SeasonResult> Download(IPlaybackProvider provider, ProviderRelease release, IReadOnlyList<ProviderEpisode> episodes,
        string root, string title, long animeId, string quality, IProgress<SeasonProgress>? progress, CancellationToken ct)
    {
        var ready = await Task.Run(() => DownloadLibrary.Read(root), ct);
        int done = 0, downloaded = 0, skipped = 0;
        var failed = new List<int>();
        foreach (var episode in episodes.DistinctBy(e => e.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (DownloadLibrary.Find(ready, provider.Id, release.Id, episode.Id) is not null) { skipped++; done++; continue; }
            var identity = new DownloadIdentity(provider.Id, release.Id, episode.Id, animeId);
            var directory = DownloadLibrary.ReleaseFolder(root, title, provider.DisplayName, identity);
            progress?.Report(new(done, episodes.Count, episode.Number));
            try
            {
                var source = await provider.GetPlayback(release.Id, episode, ct);
                await new EpisodeDownloader(http).Download(source, directory, title + " · " + provider.DisplayName + " · серия " + episode.Number,
                    quality, new TransferProgress(p => progress?.Report(new(done, episodes.Count, episode.Number, p))), ct, episode.Number, identity);
                downloaded++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or OperationCanceledException or InvalidOperationException)
            { failed.Add(episode.Number); }
            done++;
            progress?.Report(new(done, episodes.Count, episode.Number));
        }
        return new(downloaded, skipped, failed);
    }
    private sealed class TransferProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => report(value); }
}

