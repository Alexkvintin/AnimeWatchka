using System.Diagnostics;
using AnimeApp.Models;
using AnimeApp.Services.Database;

namespace AnimeApp.Services.Playback;
public enum PlaybackKind
{
    DirectMedia,
    WebPage,
    LocalFile
}

public record MediaVariant(string Quality, string Location, int? Width = null, long? Bandwidth = null)
{
    public int Height => int.TryParse(Quality.TrimEnd('p'), out var height) ? height : 0;
}

public record MediaTrack(string Location, string Language = "", string Label = "");
public record PlaybackSource(PlaybackKind Kind, string Location, IReadOnlyDictionary<string, string>? Headers = null, IReadOnlyList<MediaVariant>? Variants = null, MediaTrack? VideoTrack = null, MediaTrack? AudioTrack = null, IReadOnlyList<MediaTrack>? SubtitleTracks = null, double StartSeconds = 0, bool StartPaused = false, string? PreferredAudioLanguage = null, bool IsTorrentStream = false);
public record PlaybackRequest(long UserId, long ShikimoriId, string Title, string ProviderId, string ReleaseId, string EpisodeId, int Episode, PlaybackSource Source, double ResumeSeconds = 0, bool CountsTowardProgress = true);
public interface IPlaybackLauncher
{
    Task Open(PlaybackRequest request, CancellationToken ct = default);
}

public sealed class SystemPlaybackLauncher : IPlaybackLauncher
{
    public Task Open(PlaybackRequest r, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (r.Source.Kind != PlaybackKind.LocalFile && (!Uri.TryCreate(r.Source.Location, UriKind.Absolute, out var u) || u.Scheme != "https"))
            throw new InvalidOperationException("Разрешены только HTTPS-адреса.");
        Process.Start(new ProcessStartInfo(r.Source.Location) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

public sealed class EmbeddedPlaybackLauncher : IPlaybackLauncher
{
    public Func<PlaybackRequest, CancellationToken, Task>? OpenRequested { get; set; }

    public Task Open(PlaybackRequest r, CancellationToken ct = default) => OpenRequested?.Invoke(r, ct) ?? throw new InvalidOperationException("Встроенный проигрыватель ещё не готов.");
}

public sealed class PlaybackService(LibraryRepository repository, IPlaybackLauncher launcher)
{
    public async Task Start(PlaybackRequest request, CancellationToken ct = default)
    {
        await launcher.Open(request, ct);
        await ReportProgress(request, request.ResumeSeconds, 0, ct, eligible: false);
    }

    public async Task<bool> ReportProgress(PlaybackRequest r, double position, double duration, CancellationToken ct = default, bool eligible = true, IReadOnlyList<WatchRange>? ranges = null)
    {
        if (!double.IsFinite(position) || position < 0 || !double.IsFinite(duration) || duration < 0)
            return false;
        var settings = await repository.Setting<AppSettings>("playback", ct) ?? new();
        var mark = r.CountsTowardProgress && settings.AutoMark && eligible && duration > 0 && position / duration * 100 >= settings.Threshold;
        return await repository.PersistPlayback(r.UserId, new(r.ShikimoriId, r.Title, r.ProviderId, r.ReleaseId, r.EpisodeId, r.Episode, settings.SavePosition ? position : 0, DateTimeOffset.UtcNow, duration, mark, ranges), mark, ct);
    }
}

// Tracks actual contiguous playback, not the destination of a seek. Ranges survive restart.
public sealed class WatchTracker
{
    private readonly List<WatchRange> ranges;
    private double? previous;
    private DateTimeOffset previousTime;
    public WatchTracker(IEnumerable<WatchRange>? saved = null)
    {
        ranges = saved?.ToList() ?? [];
    }

    public IReadOnlyList<WatchRange> Ranges => ranges.ToArray();
    public double WatchedSeconds => ranges.Sum(r => r.End - r.Start);

    public void Observe(double position, bool playing, bool seek, DateTimeOffset now, double speed = 1)
    {
        if (!double.IsFinite(position) || position < 0)
            return;
        if (previous is double p && playing && !seek)
        {
            var delta = position - p;
            var elapsed = (now - previousTime).TotalSeconds;
            if (delta > 0 && delta <= Math.Max(2, elapsed * Math.Clamp(speed, 0.5, 2) * 1.75 + 1))
                Add(p, position);
        }

        previous = position;
        previousTime = now;
    }

    private void Add(double start, double end)
    {
        ranges.Add(new(start, end));
        var sorted = ranges.OrderBy(r => r.Start).ToArray();
        ranges.Clear();
        foreach (var r in sorted)
        {
            if (ranges.Count > 0 && r.Start <= ranges[^1].End + 0.25)
                ranges[^1] = ranges[^1] with
                {
                    End = Math.Max(ranges[^1].End, r.End)
                };
            else
                ranges.Add(r);
        }
    }

    public bool Eligible(double duration, int threshold) => duration > 0 && WatchedSeconds / duration * 100 >= threshold;
}
