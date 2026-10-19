using AnimeApp.Models;
using AnimeApp.Services;
using AnimeApp.Services.Database;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Shikimori;
using AnimeApp.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AnimeApp.Tests;
public sealed class PlayerViewModelTests
{
    [Fact]
    public async Task SpaceActionAtActualEndMovesNextButNearEndOnlyResumes()
    {
        var (vm, engine, repo) = await Create();
        await vm.Initialize();
        engine.Current = new(99, 100, false);
        await vm.TogglePlayCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.Request.Episode);
        engine.Current = new(100, 100, false, Ended: true);
        await vm.TogglePlayCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Request.Episode);
        Assert.Contains("Серия 2", vm.EpisodeLabel);
        Assert.Contains("2 из 2", vm.EpisodeLabel);
        Assert.False((await repo.History(0)).Single(h => h.Episode == 1).Completed);
        await vm.Close();
    }

    [Fact]
    public async Task SkipOpeningUsesCurrentEnginePositionAndClampsToDuration()
    {
        var (vm, engine, _) = await Create();
        await vm.Initialize();
        engine.Current = new(25, 300, true);
        vm.SkipOpeningCommand.Execute(null);
        Assert.Equal(115, engine.LastSeek);
        engine.Current = new(270, 300, false);
        vm.SkipOpeningCommand.Execute(null);
        Assert.Equal(300, engine.LastSeek);
        await vm.Close();
    }

    [Fact]
    public void RealPointerMovementAnywhereWakesControlsForSevenAndHalfSeconds()
    {
        var state = new FullscreenState();
        var now = DateTimeOffset.UtcNow;
        state.Enter(now);
        state.PointerMoved(10, 500, true, now);
        state.PointerMoved(10, 500, true, now.AddSeconds(10));
        Assert.False(state.ControlsVisible(now.AddSeconds(10)));
        state.PointerMoved(50, 100, false, now.AddSeconds(11));
        Assert.True(state.ControlsVisible(now.AddSeconds(11)));
        Assert.True(state.ControlsVisible(now.AddSeconds(18)));
        Assert.False(state.ControlsVisible(now.AddSeconds(19)));
        state.PointerMoved(55, 500, true, now.AddSeconds(12));
        Assert.True(state.ControlsVisible(now.AddSeconds(12)));
    }
    [Fact]
    public async Task CloseDetachesSurfaceBeforeDisposingEngine()
    {
        var(vm, engine, repo) = await Create();
        await vm.Initialize();
        engine.Current = new(50, 100, false);
        var detached = false;
        await vm.Close(() =>
        {
            Assert.False(engine.Disposed);
            detached = true;
        });
        Assert.True(detached);
        Assert.True(engine.Disposed);
        Assert.Equal(50, Assert.Single(await repo.History(0)).Position);
    }

    internal static async Task<(PlayerViewModel Vm, FakeEngine Engine, LibraryRepository Repo)> Create(bool autoResume = true, string status = "released", bool variants = false)
    {
        var db = new DatabaseService(Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString(), "player.db"));
        await db.Initialize();
        var repo = new LibraryRepository(db);
        await repo.Cache(0, [new() { ShikimoriId = 1, EpisodesTotal = 2, Status = status }]);
        await repo.SaveSetting("playback", new AppSettings(AutoResume: autoResume));
        var service = new PlaybackService(repo, new EmbeddedPlaybackLauncher());
        var request = new PlaybackRequest(0, 1, "Test", "fake", "release", "ep1", 1, new(PlaybackKind.DirectMedia, "https://example.org/video.mp4", Variants: variants ? [new("1080p","https://example.org/1080.mp4"),new("720p","https://example.org/720.mp4")] : null));
        await service.ReportProgress(request, 42, 100, eligible: false, ranges: [new(0, 42)]);
        var engine = new FakeEngine();
        var provider = new FakeProvider();
        return (new(request, await provider.GetEpisodes("release"), provider, engine, repo, service, new SyncService(repo, new NoApi()), NullLogger.Instance), engine, repo);
    }

    [Fact]
    public async Task ResumeSeeksOnceAndClosePersistsLatestPosition()
    {
        var(vm, engine, repo) = await Create();
        await vm.Initialize();
        await vm.Tick();
        Assert.Equal(42, engine.LastSeek);
        engine.Current = new(60, 100, true);
        await vm.Tick();
        Assert.Equal(1, engine.SeekCalls);
        await vm.Close();
        var history = Assert.Single(await repo.History(0));
        Assert.Equal(60, history.Position);
        Assert.False(history.Completed);
        Assert.True(engine.Disposed);
    }

    [Fact]
    public async Task ManualResumeWaitsForUser()
    {
        var(vm, engine, _) = await Create(false);
        await vm.Initialize();
        await vm.Tick();
        Assert.True(vm.ShowResume);
        Assert.Equal(0, engine.SeekCalls);
        vm.ResumeCommand.Execute(null);
        Assert.Equal(42, engine.LastSeek);
        Assert.False(vm.ShowResume);
        await vm.Close();
    }

    [Fact]
    public async Task NextSavesPreviousAndNeverInventsThirdEpisode()
    {
        var(vm, engine, repo) = await Create();
        await vm.Initialize();
        engine.Current = new(65, 100, true);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal("ep2", vm.Request.EpisodeId);
        Assert.False(vm.HasNext);
        Assert.Equal(65, Assert.Single(await repo.History(0)).Position);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal("ep2", vm.Request.EpisodeId);
        Assert.Contains("Новых серий", vm.Status);
        await vm.Close();
    }

    [Fact]
    public async Task OngoingCannotBeCompletedAtLastAvailableEpisode()
    {
        var(vm, engine, repo) = await Create(status: "airing");
        await vm.Initialize();
        await repo.PersistPlayback(0, new(1, "Test", "fake", "release", "ep2", 2, 90, DateTimeOffset.UtcNow, 100, false, [new(0, 90)]), false);
        await vm.NextCommand.ExecuteAsync(null);
        engine.Current = new(90, 100, false);
        await vm.Save();
        Assert.Equal(2, Assert.Single(await repo.Library(0)).EpisodesWatched);
        Assert.False(vm.CanComplete);
        await vm.Close();
    }

    [Fact]
    public async Task WebAdapterUsesBridgeAndTelemetry()
    {
        var web = new WebPlayerEngine();
        string? url = null;
        string? script = null;
        web.Navigate = s =>
        {
            url = s;
            return Task.CompletedTask;
        };
        web.Execute = s =>
        {
            script = s;
            return Task.CompletedTask;
        };
        Assert.False(web.Snapshot.CanControl);
        await web.Open(new(PlaybackKind.WebPage, "https://example.org/player"));
        Assert.Equal("https://example.org/player", url);
        web.Update(new(30, 100, true));
        web.Seek(42);
        Assert.Contains("seek(42)", script);
        Assert.Equal(30, web.Snapshot.Position);
        await web.DisposeAsync();
    }

    public sealed class FakeEngine : IPlayerEngine
    {
        public PlaybackSnapshot Current = new(0, 100, true);
        public PlaybackSnapshot Snapshot => Current;

        public double LastSeek;
        public int SeekCalls;
        public bool Disposed;
        public PlaybackSource? LastSource;
        public HashSet<string> FailedLocations { get; } = [];
        public Task Open(PlaybackSource source, CancellationToken ct = default)
        {
            LastSource=source;
            if (FailedLocations.Contains(source.Location)) { Current=new(0,100,false,Error:"Test failure"); throw new IOException("Test failure"); }
            Current = new(source.StartSeconds, 100, !source.StartPaused);
            return Task.CompletedTask;
        }

        public void Play() => Current = Current with
        {
            Playing = true
        };
        public void Pause() => Current = Current with
        {
            Playing = false
        };
        public void Seek(double s)
        {
            LastSeek = s;
            SeekCalls++;
            Current = Current with
            {
                Position = s
            };
        }

        public void SetVolume(int v)
        {
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeProvider : IAnimeProvider
    {
        public string Id => "fake";
        public string DisplayName => "Fake";
        public string Language => "ru";

        public Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default) => Task.FromResult(new ProviderAvailability(true, ""));
        public Task<IReadOnlyList<ProviderRelease>> Search(string q, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderRelease>>([]);
        public Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default) => Task.FromResult(new ProviderRelease(id, "Test"));
        public Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderEpisode>>([new("ep1", 1, "1"), new("ep2", 2, "2")]);
        public Task<PlaybackSource> GetPlayback(string id, ProviderEpisode e, CancellationToken ct = default) => Task.FromResult(new PlaybackSource(PlaybackKind.DirectMedia, "https://example.org/" + e.Id));
    }

    private sealed class NoApi : IShikimoriService
    {
        public Task<UserIdentity> CurrentUser(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<List<Anime>> Rates(long user, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task Update(long user, Anime anime, CancellationToken ct = default) => throw new InvalidOperationException();
    }
}
