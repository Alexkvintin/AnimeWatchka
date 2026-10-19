using System.Net;
using AnimeApp.Models;
using AnimeApp.Providers.AnimeVost;
using AnimeApp.Services;
using AnimeApp.Services.Database;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Shikimori;
using AnimeApp.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AnimeApp.Tests;
public sealed class LiveAccountTests
{
    public sealed record Recovery(long User, Anime Original);
    [AccountFact, Trait("Category", "AccountLive")]
    public async Task NativeAnimeVostOfflineRetryRealUpdateReadbackRestoreAndRefresh()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeApp");
        var credentials = new CredentialManager(new WindowsCredentialStore(Path.Combine(root, "credentials.dat")));
        await credentials.Reload();
        var tokens = new WindowsTokenStore(Path.Combine(root, "tokens.dat"));
        var oldToken = await tokens.Read();
        Assert.NotNull(oldToken);
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        var api = new ShikimoriService(http, credentials.Current, tokens, credentials);
        var user = await api.CurrentUser();
        var original = (await api.Rates(user.Id)).Single(a => a.ShikimoriId == 52991);
        Assert.Equal("watching", original.UserStatus);
        Assert.True(original.EpisodesWatched < original.EpisodesTotal);
        var realDb = new DatabaseService(Path.Combine(root, "library.db"));
        await realDb.Initialize();
        Assert.DoesNotContain(await new LibraryRepository(realDb).Pending(user.Id), p => p.AnimeId == original.ShikimoriId);
        var recoveryPath = Path.Combine(root, "stage3-progress-recovery.dat");
        Assert.False(File.Exists(recoveryPath), "A previous recovery file must be handled before another test.");
        var recovery = new ProtectedFile<Recovery>(recoveryPath);
        await recovery.Write(new(user.Id, original));
        var db = new DatabaseService(Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString(), "db"));
        await db.Initialize();
        var repo = new LibraryRepository(db);
        await repo.Cache(user.Id, [original]);
        await repo.SaveSetting("playback", new AppSettings(Threshold: 1, AutoSync: false));
        var provider = new AnimeVostProvider(http, new AnimeVostParser());
        const string release = "https://animevost.org/tip/tv/3044-sousou-no-frieren.html";
        var episodes = await provider.GetEpisodes(release);
        var episode = episodes.Single(e => e.Number == original.EpisodesWatched + 1);
        var source = await provider.GetPlayback(release, episode);
        var request = new PlaybackRequest(user.Id, original.ShikimoriId, original.Title, provider.Id, release, episode.Id, episode.Number, source);
        var offline = new SwitchApi(api);
        var sync = new SyncService(repo, offline);
        var engine = new VlcPlayerEngine("--vout=dummy", "--aout=dummy");
        var vm = new PlayerViewModel(request, episodes, provider, engine, repo, new PlaybackService(repo, new EmbeddedPlaybackLauncher()), sync, NullLogger.Instance);
        var attempted = false;
        try
        {
            await vm.Initialize();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(70);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await vm.Tick();
                Assert.Null(engine.Snapshot.Error);
                if ((await repo.Pending(user.Id)).Count > 0)
                    break;
                await Task.Delay(200);
            }

            await vm.Save();
            await vm.Close();
            Assert.True(Assert.Single(await repo.History(user.Id)).Completed);
            Assert.Equal(original.EpisodesWatched + 1, Assert.Single(await repo.Pending(user.Id)).Episodes);
            await Assert.ThrowsAsync<IOException>(() => sync.Synchronize(user.Id));
            Assert.Single(await repo.Pending(user.Id));
            offline.Offline = false;
            attempted = true;
            await sync.Synchronize(user.Id);
            Assert.Empty(await repo.Pending(user.Id));
            var verified = (await api.Rates(user.Id)).Single(a => a.ShikimoriId == original.ShikimoriId);
            Assert.Equal(original.EpisodesWatched + 1, verified.EpisodesWatched);
            Assert.Equal(original.UserStatus, verified.UserStatus);
            Assert.Equal(original.Score, verified.Score);
            Assert.Equal(original.Rewatches, verified.Rewatches);
            var writes = offline.Writes;
            await sync.Synchronize(user.Id);
            Assert.Equal(writes, offline.Writes);
        }
        finally
        {
            await vm.Close();
            if (attempted)
            {
                Assert.Equal(user.Id, (await api.CurrentUser()).Id);
                var current = (await api.Rates(user.Id)).Single(a => a.ShikimoriId == original.ShikimoriId);
                if (current.EpisodesWatched != original.EpisodesWatched || current.UserStatus != original.UserStatus)
                {
                    Assert.Equal(original.EpisodesWatched + 1, current.EpisodesWatched);
                    Assert.Equal(original.UserStatus, current.UserStatus);
                    await api.Update(user.Id, original);
                }

                var restored = (await api.Rates(user.Id)).Single(a => a.ShikimoriId == original.ShikimoriId);
                Assert.Equal(original.EpisodesWatched, restored.EpisodesWatched);
                Assert.Equal(original.UserStatus, restored.UserStatus);
                Assert.Equal(original.Score, restored.Score);
                Assert.Equal(original.Rewatches, restored.Rewatches);
            }

            await recovery.Clear();
        }

        // Only the first 401 is simulated. The refresh POST and retried whoami reach Shikimori.
        using var handler = new FirstUnauthorized();
        using var refreshHttp = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        var refreshApi = new ShikimoriService(refreshHttp, credentials.Current, tokens, credentials);
        Assert.Equal(user.Id, (await refreshApi.CurrentUser()).Id);
        Assert.True(handler.Injected);
        Assert.True(handler.RefreshReached);
        var newToken = await new WindowsTokenStore(Path.Combine(root, "tokens.dat")).Read();
        Assert.NotNull(newToken);
        Assert.NotEqual(oldToken.AccessToken, newToken.AccessToken);
        await new ProtectedFile<string[]>(Path.Combine(root, "stage3-audit-secrets.dat")).Write([oldToken.AccessToken, oldToken.RefreshToken, newToken.AccessToken, newToken.RefreshToken]);
    }

    private sealed class SwitchApi(IShikimoriService api) : IShikimoriService
    {
        public bool Offline = true;
        public int Writes;
        public Task<UserIdentity> CurrentUser(CancellationToken ct = default) => Offline ? throw new IOException("Test offline transport") : api.CurrentUser(ct);
        public Task<List<Anime>> Rates(long user, CancellationToken ct = default) => api.Rates(user, ct);
        public Task<Anime?> ReadRate(long user, Anime anime, CancellationToken ct = default) => api.ReadRate(user, anime, ct);
        public Task UpdateFields(long user, Anime anime, PendingChange p, CancellationToken ct = default)
        {
            Writes++;
            return api.UpdateFields(user, anime, p, ct);
        }

        public Task Update(long user, Anime anime, CancellationToken ct = default)
        {
            Writes++;
            return api.Update(user, anime, ct);
        }
    }

    private sealed class FirstUnauthorized : DelegatingHandler
    {
        public bool Injected;
        public bool RefreshReached;
        public FirstUnauthorized() : base(new HttpClientHandler())
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (!Injected && r.RequestUri!.AbsolutePath == "/api/users/whoami")
            {
                Injected = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            if (r.RequestUri!.AbsolutePath == "/oauth/token")
                RefreshReached = true;
            return base.SendAsync(r, ct);
        }
    }
}

public sealed class AccountFactAttribute : FactAttribute
{
    public AccountFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ANIMEAPP_ACCOUNT_TESTS") != "1")
            Skip = "Explicit opt-in: mutates one real rate, read-backs and restores it; application must be closed.";
    }
}
