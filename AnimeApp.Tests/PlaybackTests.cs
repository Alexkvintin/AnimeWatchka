using AnimeApp.Models;
using AnimeApp.Services.Database;
using AnimeApp.Services.Playback;
using Xunit;

namespace AnimeApp.Tests;
public sealed class PlaybackTests
{
    private static async Task<LibraryRepository> Repo()
    {
        var db = new DatabaseService(Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString(), "db"));
        await db.Initialize();
        return new(db);
    }

    private static PlaybackRequest Request(int episode) => new(0, 1, "Test", "provider", "release", "ep" + episode, episode, new(PlaybackKind.DirectMedia, "https://video.example/test.mp4"));
    [Fact]
    public async Task MappingCanBeReplacedAndDeleted()
    {
        var r = await Repo();
        await r.SaveMapping(new(1, "provider", "old"));
        await r.SaveMapping(new(1, "provider", "new"));
        Assert.Equal("new", await r.Mapping(1, "provider"));
        await r.DeleteMapping(1, "provider");
        Assert.Null(await r.Mapping(1, "provider"));
    }

    [Fact]
    public async Task ThresholdIsAbsoluteAndIdempotent()
    {
        var r = await Repo();
        await r.Cache(0, [new() { ShikimoriId = 1, EpisodesTotal = 28, EpisodesWatched = 14, UserStatus = "watching" }]);
        var s = new PlaybackService(r, new FakeLauncher());
        await s.ReportProgress(Request(15), 89, 100);
        Assert.Equal(14, Assert.Single(await r.Library(0)).EpisodesWatched);
        await s.ReportProgress(Request(15), 90, 100);
        await s.ReportProgress(Request(15), 95, 100);
        Assert.Equal(15, Assert.Single(await r.Library(0)).EpisodesWatched);
        Assert.Single(await r.Pending(0));
        await s.ReportProgress(Request(8), 95, 100);
        Assert.Equal(15, Assert.Single(await r.Library(0)).EpisodesWatched);
    }

    [Fact]
    public async Task ResumeRangesAndPositionSurviveRead()
    {
        var r = await Repo();
        var s = new PlaybackService(r, new FakeLauncher());
        await s.ReportProgress(Request(6), 1122, 1450, eligible: false, ranges: [new(0, 100), new(1000, 1122)]);
        var h = Assert.Single(await r.History(0));
        Assert.Equal(1122, h.Position);
        Assert.Equal(1450, h.Duration);
        Assert.Equal(222, new WatchTracker(h.WatchedRanges).WatchedSeconds);
    }

    [Fact]
    public void SeekDoesNotCountAsWatching()
    {
        var t = new WatchTracker();
        var now = DateTimeOffset.UtcNow;
        t.Observe(0, true, false, now);
        t.Observe(95, true, true, now.AddSeconds(1));
        t.Observe(96, true, false, now.AddSeconds(2));
        Assert.False(t.Eligible(100, 90));
        Assert.Equal(1, t.WatchedSeconds);
    }

    [Fact]
    public void ContinuousPlaybackAndResumeReachExactThreshold()
    {
        var now = DateTimeOffset.UtcNow;
        var t = new WatchTracker();
        for (int i = 0; i <= 89; i++)
            t.Observe(i, true, false, now.AddSeconds(i));
        Assert.False(t.Eligible(100, 90));
        var resumed = new WatchTracker(t.Ranges);
        resumed.Observe(89, true, true, now.AddSeconds(100));
        resumed.Observe(90, true, false, now.AddSeconds(101));
        Assert.True(resumed.Eligible(100, 90));
    }

    [Theory]
    [InlineData(PlaybackKind.DirectMedia)]
    [InlineData(PlaybackKind.LocalFile)]
    [InlineData(PlaybackKind.WebPage)]
    public async Task LauncherPreservesPlaybackKind(PlaybackKind kind)
    {
        var r = await Repo();
        var l = new FakeLauncher();
        await new PlaybackService(r, l).Start(Request(1)with { Source = new(kind, kind == PlaybackKind.LocalFile ? "C:\\video.mp4" : "https://example.org/video") });
        Assert.Equal(kind, l.Last!.Source.Kind);
    }

    [Fact]
    public async Task UnknownDurationOrSeekCannotMark()
    {
        var r = await Repo();
        await r.Cache(0, [new() { ShikimoriId = 1, EpisodesWatched = 0, EpisodesTotal = 12 }]);
        var s = new PlaybackService(r, new FakeLauncher());
        await s.ReportProgress(Request(1), 95, 100, eligible: false);
        await s.ReportProgress(Request(1), 95, 0);
        Assert.Empty(await r.Pending(0));
    }

    private sealed class FakeLauncher : IPlaybackLauncher
    {
        public PlaybackRequest? Last;
        public Task Open(PlaybackRequest r, CancellationToken ct = default)
        {
            Last = r;
            return Task.CompletedTask;
        }
    }
}
