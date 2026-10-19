using AnimeApp.Models;
using AnimeApp.Services;
using AnimeApp.Services.Database;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Playback;
using Xunit;

namespace AnimeApp.Tests;
public sealed class ContinuationTests
{
    private static async Task<LibraryRepository> Repository()
    {
        var db = new DatabaseService(Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString(), "db"));
        await db.Initialize();
        return new(db);
    }

    private sealed class Provider(params ProviderEpisode[] episodes) : IAnimeProvider
    {
        public string Id => "test";
        public string DisplayName => "Test source";
        public string Language => "";

        public Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default) => Task.FromResult(new ProviderAvailability(true, ""));
        public Task<IReadOnlyList<ProviderRelease>> Search(string q, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderRelease>>([]);
        public Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default) => Task.FromResult(new ProviderRelease(id, "Release"));
        public Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderEpisode>>(episodes);
        public Task<PlaybackSource> GetPlayback(string id, ProviderEpisode e, CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task MissingMappingShowsSelectionInsteadOfFakeContinue()
    {
        var r = await Repository();
        var plan = await new ContinuationService(r).Find(new() { ShikimoriId = 1 }, [], [new Provider(new ProviderEpisode("1", 1, "1"))]);
        Assert.False(plan.Available);
        Assert.Equal("Открыть список серий", plan.Hint);
    }

    [Fact]
    public async Task ResumeUsesSavedPositionAndReleaseNotGuessedEpisode()
    {
        var r = await Repository();
        await r.SaveMapping(new(1, "test", "release"));
        var provider = new Provider(new ProviderEpisode("e3", 3, "3"), new("e4", 4, "4"));
        var entry = new WatchEntry(1, "Title", "test", "release", "e3", 3, 78, DateTimeOffset.UtcNow);
        var plan = await new ContinuationService(r).Find(new() { ShikimoriId = 1, EpisodesWatched = 2 }, [entry], [provider]);
        Assert.Equal("e3", plan.Episode!.Id);
        Assert.Equal(78, plan.Position);
        Assert.Contains("01:18", plan.Label);
    }

    [Fact]
    public async Task LastAvailableDoesNotInventNextEpisode()
    {
        var r = await Repository();
        await r.SaveMapping(new(1, "test", "release"));
        var plan = await new ContinuationService(r).Find(new() { ShikimoriId = 1, EpisodesWatched = 2, EpisodesTotal = 12 }, [], [new Provider(new ProviderEpisode("e2", 2, "2"))]);
        Assert.False(plan.Available);
        Assert.Contains("пока нет", plan.Hint);
    }

    [Fact]
    public async Task UnavailableEpisodeAndWrongReleaseHistoryAreIgnored()
    {
        var r = await Repository();
        await r.SaveMapping(new(1, "test", "release"));
        var entry = new WatchEntry(1, "Title", "test", "old-release", "e1", 1, 78, DateTimeOffset.UtcNow);
        var plan = await new ContinuationService(r).Find(new() { ShikimoriId = 1, EpisodesWatched = 1 }, [entry], [new Provider(new ProviderEpisode("e2", 2, "2", Available: false), new("e3", 3, "3"))]);
        Assert.Equal(3, plan.Episode!.Number);
        Assert.Equal(0, plan.Position);
    }
}
