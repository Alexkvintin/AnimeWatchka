using System.Net;
using System.Text;
using AnimeApp.Models;
using AnimeApp.Services;
using AnimeApp.Services.Database;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Shikimori;
using AnimeApp.ViewModels;
using Xunit;

namespace AnimeApp.Tests;
public sealed class Stage4Tests
{
    private static Anime A => new()
    {
        ShikimoriId = 1,
        RateId = 42,
        Name = "Test",
        EpisodesWatched = 4,
        UserStatus = "watching",
        Score = 8,
        Rewatches = 2
    };

    private sealed class Tokens : ITokenStore
    {
        public Task<OAuthToken?> Read(CancellationToken ct = default) => Task.FromResult<OAuthToken?>(new("test", "test", DateTimeOffset.MaxValue));
        public Task Write(OAuthToken t, CancellationToken ct = default) => Task.CompletedTask;
        public Task Clear(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => callback(r);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
    private static ShikimoriService Api(Func<HttpRequestMessage, Task<HttpResponseMessage>> f) => new(new(new Handler(f)), new(), new Tokens());
    private static async Task<LibraryRepository> Repo()
    {
        var db = new DatabaseService(Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString(), "db"));
        await db.Initialize();
        return new(db);
    }

    [Theory]
    [InlineData("score")]
    [InlineData("episodes")]
    [InlineData("status")]
    public async Task PatchContainsOnlyChangedField(string field)
    {
        var r = await Repo();
        var after = field switch
        {
            "score" => A with
            {
                Score = 9
            },
            "status" => A with
            {
                UserStatus = "on_hold"
            },
            _ => A with
            {
                EpisodesWatched = 5
            }
        };
        await r.Change(7, A, after);
        var p = Assert.Single(await r.Pending(7));
        string body = "";
        var api = Api(async req =>
        {
            Assert.Equal(HttpMethod.Patch, req.Method);
            body = Uri.UnescapeDataString(await req.Content!.ReadAsStringAsync());
            return Json("{}");
        });
        await api.UpdateFields(7, A, p);
        Assert.StartsWith($"user_rate[{field}]=", body);
        Assert.DoesNotContain("&", body);
        Assert.DoesNotContain("rewatches", body);
    }

    [Fact]
    public async Task PendingCoalescesScoreAndProgressWithoutLosingBaseline()
    {
        var r = await Repo();
        await r.Change(7, A, A with { Score = 9 });
        await r.Change(7, A with { Score = 9 }, A with { Score = 9, EpisodesWatched = 5 });
        var p = Assert.Single(await r.Pending(7));
        Assert.True(p.EpisodesChanged);
        Assert.False(p.StatusChanged);
        Assert.Equal(9, p.Score);
        Assert.Equal(8, p.BaseScore);
        Assert.Equal(4, p.BaseEpisodes);
    }

    [Fact]
    public async Task AcknowledgingOlderRevisionRebasesNewerPendingScore()
    {
        var r = await Repo();
        await r.Change(7, A, A with { Score = 9 });
        var old = Assert.Single(await r.Pending(7));
        await r.Change(7, A with { Score = 9 }, A with { Score = 10 });
        await r.Ack(old);
        var newer = Assert.Single(await r.Pending(7));
        Assert.Equal(9, newer.BaseScore);
        Assert.Equal(10, newer.Score);
        var api = new SyncApi
        {
            Value = A with
            {
                Score = 9
            }
        };
        await new SyncService(r, api).Synchronize(7);
        Assert.Empty(await r.Pending(7));
        Assert.Equal(10, api.Value.Score);
    }

    private sealed class SyncApi : IShikimoriService
    {
        public Anime Value = A;
        public int FullReads, TargetReads, Writes;
        public bool IgnoreWrite;
        public Task<UserIdentity> CurrentUser(CancellationToken ct = default) => Task.FromResult(new UserIdentity(7, "test"));
        public Task<List<Anime>> Rates(long user, CancellationToken ct = default)
        {
            FullReads++;
            return Task.FromResult(new List<Anime> { Value });
        }

        public Task<Anime?> ReadRate(long user, Anime anime, CancellationToken ct = default)
        {
            TargetReads++;
            return Task.FromResult<Anime?>(Value);
        }

        public Task Update(long user, Anime anime, CancellationToken ct = default) => throw new Exception("Full object update is forbidden");
        public Task UpdateFields(long user, Anime a, PendingChange p, CancellationToken ct = default)
        {
            Writes++;
            if (!IgnoreWrite)
                Value = Value with
                {
                    Score = p.Score ?? Value.Score,
                    EpisodesWatched = p.EpisodesChanged ? p.Episodes : Value.EpisodesWatched,
                    UserStatus = p.StatusChanged ? p.Status : Value.UserStatus
                };
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ScoreSyncPreservesRemoteProgressAndUsesTargetedReadback()
    {
        var r = await Repo();
        await r.Change(7, A, A with { Score = 9 });
        var api = new SyncApi
        {
            Value = A with
            {
                EpisodesWatched = 6,
                UserStatus = "on_hold"
            }
        };
        var sync = new SyncService(r, api);
        await sync.Synchronize(7);
        Assert.Empty(sync.Conflicts);
        Assert.Empty(await r.Pending(7));
        Assert.Equal(1, api.FullReads);
        Assert.Equal(1, api.TargetReads);
        Assert.Equal(6, api.Value.EpisodesWatched);
        Assert.Equal("on_hold", api.Value.UserStatus);
        Assert.Equal(9, api.Value.Score);
        Assert.Equal(2, api.Value.Rewatches);
        await sync.Synchronize(7);
        Assert.Equal(1, api.Writes);
    }

    [Fact]
    public async Task UnconfirmedScoreRemainsQueued()
    {
        var r = await Repo();
        await r.Change(7, A, A with { Score = 9 });
        var api = new SyncApi
        {
            IgnoreWrite = true
        };
        await new SyncService(r, api).Synchronize(7);
        Assert.Single(await r.Pending(7));
    }

    [Fact]
    public async Task ProgressSyncPreservesServerScore()
    {
        var r = await Repo();
        await r.Change(7, A, A with { EpisodesWatched = 5 });
        var api = new SyncApi
        {
            Value = A with
            {
                Score = 10
            }
        };
        await new SyncService(r, api).Synchronize(7);
        Assert.Equal(10, api.Value.Score);
        Assert.Empty(await r.Pending(7));
    }

    [Fact]
    public async Task TargetReadValidatesAccountAndParsesScore()
    {
        var api = Api(req =>
        {
            Assert.Equal("/api/v2/user_rates/42", req.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("{\"id\":42,\"user_id\":7,\"target_id\":1,\"target_type\":\"Anime\",\"score\":9,\"episodes\":4,\"status\":\"watching\",\"rewatches\":2}"));
        });
        Assert.Equal(9, (await api.ReadRate(7, A))!.Score);
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.ReadRate(8, A));
    }

    [Fact]
    public async Task UnavailableTargetEndpointFallsBackToList()
    {
        var paths = new List<string>();
        var api = Api(req =>
        {
            paths.Add(req.RequestUri!.AbsolutePath);
            return Task.FromResult(paths.Count == 1 ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json("[]"));
        });
        Assert.Null(await api.ReadRate(7, A));
        Assert.Equal(2, paths.Count);
        Assert.Contains("/api/v2/user_rates/42", paths);
    }

    [Theory]
    [InlineData("prequel", "tv", true)]
    [InlineData("sequel", "tv", true)]
    [InlineData("sequel", "movie", false)]
    [InlineData("side_story", "tv", false)]
    [InlineData("prequel", "special", false)]
    public void SeasonClassificationKeepsExtrasSeparate(string relation, string kind, bool season) => Assert.Equal(season, new AnimeRelation(A with { Kind = kind }, relation).IsSeason);
    [Fact]
    public async Task MetadataRelationsKeepNavigableAnimeIdentity()
    {
        IMetadataProvider provider = Api(_ => Task.FromResult(Json("[{\"relation\":\"Sequel\",\"anime\":{\"id\":2,\"name\":\"Next\",\"kind\":\"tv\"}},{\"relation\":\"Adaptation\",\"anime\":null}]")));
        var r = Assert.Single(await provider.Relations(1));
        Assert.True(r.IsSeason);
        Assert.Equal(new AnimeIdentity("shikimori", "2"), r.Anime.Identity);
    }

    [Fact]
    public void FullscreenAutoHideResetsAndRestoresControls()
    {
        var s = new FullscreenState();
        var t = DateTimeOffset.UtcNow;
        Assert.True(s.ControlsVisible(t));
        s.Enter(t);
        Assert.True(s.Active);
        Assert.False(s.ControlsVisible(t.AddSeconds(8)));
        s.Interact(t.AddSeconds(9));
        Assert.True(s.ControlsVisible(t.AddSeconds(10)));
        s.Exit();
        Assert.False(s.Active);
        Assert.True(s.ControlsVisible(t.AddHours(1)));
    }

    [Fact]
    public void ScheduleWeekStartsMondayAndFiltersLocalDay()
    {
        Assert.Equal(DayOfWeek.Monday, ScheduleViewModel.Week[0].Day);
        Assert.Equal(DayOfWeek.Sunday, ScheduleViewModel.Week[6].Day);
        var day = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var item = new ScheduleItem(A, 5, day);
        Assert.Single(ScheduleViewModel.ForDay([item], day.ToLocalTime().DayOfWeek));
        Assert.Contains("ещё не проверена", item.Availability);
    }

    [Theory]
    [InlineData("https://animevost.org", "text/html", 200, SourceSupport.Known)]
    [InlineData("https://example.org", "text/html", 200, SourceSupport.Web)]
    [InlineData("https://example.org", "application/json", 200, SourceSupport.AdapterRequired)]
    [InlineData("https://example.org", "text/html", 403, SourceSupport.Unavailable)]
    [InlineData("http://example.org", "text/html", 200, SourceSupport.AdapterRequired)]
    public async Task ProbeReportsActualCapabilities(string url, string type, int code, SourceSupport expected)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("test", Encoding.UTF8, type) })));
        Assert.Equal(expected, (await new SourceProbeService(http).Probe(url)).Support);
    }

    [Fact]
    public async Task ProbeNetworkFailureIsHumanReadable()
    {
        using var http = new HttpClient(new Handler(_ => throw new HttpRequestException()));
        Assert.Equal(SourceSupport.Unavailable, (await new SourceProbeService(http).Probe("https://example.org")).Support);
    }

    [Fact]
    public async Task GenericProviderNeverInventsEpisodesOrSearchResults()
    {
        IPlaybackProvider p = new GenericWebProvider(new("web-test", "Test", "https://example.org"), new HttpClient());
        Assert.Empty(await p.Search("Test"));
        Assert.Empty(await p.GetEpisodes("test"));
        Assert.Equal(PlaybackKind.WebPage, (await p.GetPlayback("", new("", 0, ""))).Kind);
        await Assert.ThrowsAsync<ProviderException>(() => p.GetRelease("anything"));
    }
}
