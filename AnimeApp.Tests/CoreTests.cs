using AnimeApp.Models;
using AnimeApp.Services;
using AnimeApp.Services.Database;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Shikimori;
using AnimeApp.Providers;
using AnimeApp.Providers.AnimeVost;
using Xunit;

namespace AnimeApp.Tests;
public sealed class CoreTests
{
    [Fact]
    public async Task WrongAccountCannotSendQueuedChanges()
    {
        var r = await Repository();
        var a = Anime();
        await r.Change(8, a, a with { EpisodesWatched = 5 });
        var api = new FakeApi(a);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SyncService(r, api).Synchronize(8));
        Assert.Equal(0, api.Writes);
        Assert.Single(await r.Pending(8));
    }

    [Fact]
    public async Task RemoteDeletionIsAConflict()
    {
        var r = await Repository();
        var a = Anime();
        await r.Change(7, a, a with { EpisodesWatched = 5 });
        var result = await new SyncService(r, new DeletedApi()).Synchronize(7);
        Assert.Contains("Конфликт", result);
        Assert.Single(await r.Pending(7));
    }

    private sealed class DeletedApi : IShikimoriService
    {
        public Task<UserIdentity> CurrentUser(CancellationToken ct = default) => Task.FromResult(new UserIdentity(7, "Test"));
        public Task<List<Anime>> Rates(long user, CancellationToken ct = default) => Task.FromResult(new List<Anime>());
        public Task Update(long user, Anime anime, CancellationToken ct = default) => throw new InvalidOperationException("Deleted entry must not be recreated.");
    }

    private static async Task<LibraryRepository> Repository()
    {
        var db = new DatabaseService(Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString(), "db.sqlite"));
        await db.Initialize();
        return new(db);
    }

    private static Anime Anime(int episodes = 2) => new()
    {
        ShikimoriId = 1,
        Name = "Test",
        EpisodesWatched = episodes,
        EpisodesTotal = 12,
        UserStatus = "watching",
        RateId = 11
    };
    [Fact]
    public async Task OfflineEditIsAtomicCoalescedAndAccountScoped()
    {
        var r = await Repository();
        var a = Anime();
        await r.Change(7, a, a with { EpisodesWatched = 3 });
        await r.Change(7, a with { EpisodesWatched = 3 }, a with { EpisodesWatched = 5 });
        var p = Assert.Single(await r.Pending(7));
        Assert.Equal(2, p.BaseEpisodes);
        Assert.Equal(5, p.Episodes);
        Assert.Equal(5, Assert.Single(await r.Library(7)).EpisodesWatched);
        Assert.Empty(await r.Library(8));
        Assert.Empty(await r.Pending(8));
    }

    [Fact]
    public async Task AcknowledgmentDoesNotDeleteNewerEdit()
    {
        var r = await Repository();
        var a = Anime();
        await r.Change(7, a, a with { EpisodesWatched = 3 });
        var stale = Assert.Single(await r.Pending(7));
        await r.Change(7, a, a with { EpisodesWatched = 4 });
        await r.Ack(stale);
        Assert.Equal(4, Assert.Single(await r.Pending(7)).Episodes);
    }

    [Fact]
    public async Task SyncRetriesAbsoluteValuesAndKeepsConflicts()
    {
        var r = await Repository();
        var a = Anime();
        var api = new FakeApi(a);
        var s = new SyncService(r, api);
        await r.Change(7, a, a with { EpisodesWatched = 4 });
        await s.Synchronize(7);
        Assert.Equal(4, api.Value.EpisodesWatched);
        Assert.Equal(1, api.Writes);
        Assert.Empty(await r.Pending(7));
        await s.Synchronize(7);
        Assert.Equal(1, api.Writes);
        await r.Change(7, api.Value, api.Value with { EpisodesWatched = 5 });
        api.Value = api.Value with
        {
            EpisodesWatched = 6
        };
        Assert.Contains("Конфликт", await s.Synchronize(7));
        Assert.Single(await r.Pending(7));
        Assert.Equal(5, Assert.Single(await r.Library(7)).EpisodesWatched);
        await s.AcceptRemote(7);
        Assert.Equal(6, Assert.Single(await r.Library(7)).EpisodesWatched);
        Assert.Empty(await r.Pending(7));
    }

    [Fact]
    public async Task LostSuccessResponseIsAcknowledgedWithoutSecondWrite()
    {
        var r = await Repository();
        var a = Anime();
        var api = new FakeApi(a with { EpisodesWatched = 4 });
        await r.Change(7, a, a with { EpisodesWatched = 4 });
        await new SyncService(r, api).Synchronize(7);
        Assert.Empty(await r.Pending(7));
        Assert.Equal(0, api.Writes);
    }

    [Fact]
    public async Task PullRemovesDeletedRemoteButPreservesPending()
    {
        var r = await Repository();
        await r.Cache(7, [Anime(), Anime()with { ShikimoriId = 2 }]);
        await r.Change(7, Anime(), Anime(4));
        await r.ReplaceRemote(7, []);
        Assert.Equal(1, Assert.Single(await r.Library(7)).ShikimoriId);
    }

    [Fact]
    public async Task PlaybackThresholdAndResumeArePersisted()
    {
        var r = await Repository();
        await r.Cache(7, [Anime()]);
        var p = new PlaybackService(r, new FakeLauncher());
        var req = new PlaybackRequest(7, 1, "Test", "local", "release", "episode", 3, new(PlaybackKind.LocalFile, "test.mp4"));
        await p.ReportProgress(req, 89, 100);
        Assert.Empty(await r.Pending(7));
        await p.ReportProgress(req, 91, 100);
        Assert.Equal(3, Assert.Single(await r.Pending(7)).Episodes);
        Assert.Equal(91, Assert.Single(await r.History(7)).Position);
        await p.ReportProgress(req, 95, 100);
        Assert.Single(await r.Pending(7));
    }

    [Fact]
    public async Task LocalProviderSortsNaturallyAndRejectsOutsideFiles()
    {
        var r = await Repository();
        var folder = Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "Episode 10.mp4"), "");
        await File.WriteAllTextAsync(Path.Combine(folder, "Episode 2.mp4"), "");
        await r.SaveSetting("playback", new AppSettings(LocalFolder: folder));
        var p = new LocalFilesProvider(r);
        var episodes = await p.GetEpisodes(folder);
        Assert.Contains("2", episodes[0].Title);
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.GetEpisodes(Path.GetTempPath()));
    }

    [Fact]
    public async Task MappingAndSettingsSurviveRead()
    {
        var r = await Repository();
        await r.SaveMapping(new(52991, "animevost", "12345"));
        Assert.Equal("12345", await r.Mapping(52991, "animevost"));
        await r.SaveSetting("playback", new AppSettings(Threshold: 95));
        Assert.Equal(95, (await r.Setting<AppSettings>("playback"))!.Threshold);
    }

    [Fact]
    public void AnimeVostParserRestrictsLinks()
    {
        var p = new AnimeVostParser();
        var result = p.SearchResults("<h2><a href=\"https://animevost.org/tip/tv/1.html\">Title &amp; name</a></h2><h2><a href=\"https://evil.test/a\">Bad</a></h2>");
        Assert.Equal("Title & name", Assert.Single(result).Title);
    }

    private sealed class FakeApi(Anime value) : IShikimoriService
    {
        public Anime Value = value;
        public int Writes;
        public Task<UserIdentity> CurrentUser(CancellationToken ct = default) => Task.FromResult(new UserIdentity(7, "Test"));
        public Task<List<Anime>> Rates(long user, CancellationToken ct = default) => Task.FromResult(new List<Anime> { Value });
        public Task Update(long user, Anime a, CancellationToken ct = default)
        {
            Value = a;
            Writes++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLauncher : IPlaybackLauncher
    {
        public Task Open(PlaybackRequest request, CancellationToken ct = default) => Task.CompletedTask;
    }
}
