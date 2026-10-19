using System.Text;
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
public sealed class NativePlaybackTests
{
    [NativeFact, Trait("Category", "Native")]
    public async Task NativeClockReachesThresholdAndPersistsAbsoluteProgress()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "clock.wav");
        // Generated silent PCM, no network or copyrighted media. LibVLC owns the real playback clock.
        const int bytes = 8000 * 2 * 20;
        using (var writer = new BinaryWriter(File.Create(file)))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + bytes);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(8000);
            writer.Write(16000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(bytes);
            writer.Write(new byte[bytes]);
        }

        var db = new DatabaseService(Path.Combine(folder, "db"));
        await db.Initialize();
        var repo = new LibraryRepository(db);
        await repo.Cache(0, [new() { ShikimoriId = 1, EpisodesTotal = 1, Status = "released" }]);
        var engine = new VlcPlayerEngine("--vout=dummy", "--aout=dummy");
        var source = new PlaybackSource(PlaybackKind.LocalFile, file);
        var request = new PlaybackRequest(0, 1, "Native clock", "local", "release", file, 1, source);
        var provider = new SingleProvider(source);
        var vm = new PlayerViewModel(request, await provider.GetEpisodes("release"), provider, engine, repo, new PlaybackService(repo, new EmbeddedPlaybackLauncher()), new SyncService(repo, new NoApi()), NullLogger.Instance);
        try
        {
            await vm.Initialize();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await vm.Tick();
                if (engine.Snapshot.Ended)
                    break;
                Assert.Null(engine.Snapshot.Error);
                await Task.Delay(100);
            }

            Assert.True(engine.Snapshot.Ended);
            await vm.Save();
            Assert.True(Assert.Single(await repo.History(0)).Completed);
            Assert.Equal(1, Assert.Single(await repo.Library(0)).EpisodesWatched);
            Assert.Single(await repo.Pending(0));
            Assert.True(vm.CanComplete);
        }
        finally
        {
            await vm.Close();
        }
    }

    private sealed class SingleProvider(PlaybackSource source) : IAnimeProvider
    {
        public string Id => "local";
        public string DisplayName => "Local";
        public string Language => "";

        public Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default) => Task.FromResult(new ProviderAvailability(true, ""));
        public Task<IReadOnlyList<ProviderRelease>> Search(string q, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderRelease>>([]);
        public Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default) => Task.FromResult(new ProviderRelease(id, "Native"));
        public Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderEpisode>>([new(source.Location, 1, "1")]);
        public Task<PlaybackSource> GetPlayback(string id, ProviderEpisode episode, CancellationToken ct = default) => Task.FromResult(source);
    }

    private sealed class NoApi : IShikimoriService
    {
        public Task<UserIdentity> CurrentUser(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<List<Anime>> Rates(long user, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task Update(long user, Anime anime, CancellationToken ct = default) => throw new InvalidOperationException();
    }
}

public sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ANIMEAPP_NATIVE_TESTS") != "1")
            Skip = "Для LibVLC smoke test задайте ANIMEAPP_NATIVE_TESTS=1 на Windows.";
    }
}
