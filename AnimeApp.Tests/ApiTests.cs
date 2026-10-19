using System.Net;
using System.Text;
using AnimeApp.Models;
using AnimeApp.Services.Shikimori;
using Xunit;

namespace AnimeApp.Tests;
public sealed class ApiTests
{
    [Fact]
    public async Task RatesKeepActualAdditionDateAndLeaveMissingDateUnknown()
    {
        var api = new ShikimoriService(new(new Handler(_ => Json("""
            [{"id":10,"created_at":"2020-05-01T10:00:00+03:00","anime":{"id":1,"name":"A"}},
             {"id":11,"anime":{"id":2,"name":"B"}}]
            """))), new(), new MemoryTokens());
        var rates = await api.Rates(7);
        Assert.Equal(DateTimeOffset.Parse("2020-05-01T10:00:00+03:00"), rates[0].AddedAt);
        Assert.Null(rates[1].AddedAt);
    }

    [Theory]
    [InlineData("{\"id\":7,\"nickname\":\"Alex\",\"avatar\":\"https://shikimori.io/small.png\",\"image\":{\"x80\":\"https://shikimori.io/large.png\"}}", "https://shikimori.io/large.png")]
    [InlineData("{\"id\":7,\"nickname\":\"Alex\",\"avatar\":\"/avatar.png\"}", "https://shikimori.io/avatar.png")]
    [InlineData("{\"id\":7,\"nickname\":\"Alex\",\"avatar\":\"file:///private.png\"}", "")]
    [InlineData("{\"id\":7,\"nickname\":\"Alex\"}", "")]
    public async Task IdentityReadsSafeAvatar(string json, string expected)
    {
        var api = new ShikimoriService(new(new Handler(_ => Json(json))), new(), new MemoryTokens());
        Assert.Equal(expected, (await api.CurrentUser()).Avatar);
    }

    [Fact]
    public async Task ModernPostersAreBatchedAndMissingImagesAreIgnored()
    {
        var handler = new AsyncHandler(async req =>
        {
            Assert.Equal("/api/graphql", req.RequestUri!.AbsolutePath);
            Assert.Null(req.Headers.Authorization);
            Assert.Contains("1,2,3", await req.Content!.ReadAsStringAsync());
            return Json("""{"data":{"animes":[{"id":"1","poster":{"originalUrl":"https://shikimori.io/uploads/poster/animes/1/cover.jpg"}},{"id":"2","poster":null},{"id":"3","poster":{"originalUrl":"https://shikimori.io/assets/globals/missing_original.jpg"}}]}}""");
        });
        var result = await new ShikimoriService(new(handler), new(), new MemoryTokens()).Posters([1, 2, 3, 1, 0]);
        Assert.Single(result);
        Assert.Contains("/uploads/poster/", result[1]);
    }
    [Fact]
    public async Task TimeoutAndOfflineErrorsAreReadable()
    {
        var offline = new ShikimoriService(new(new Handler(_ => throw new HttpRequestException("offline"))), new(), new MemoryTokens());
        Assert.Contains("Нет соединения", (await Assert.ThrowsAsync<IOException>(() => offline.CurrentUser())).Message);
        var timeout = new ShikimoriService(new(new Handler(_ => throw new TaskCanceledException())), new(), new MemoryTokens());
        Assert.Contains("25 секунд", (await Assert.ThrowsAsync<IOException>(() => timeout.CurrentUser())).Message);
    }

    [Fact]
    public async Task UnauthorizedRefreshesAndRetries()
    {
        var store = new MemoryTokens();
        var requests = new List<string>();
        var handler = new Handler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            requests.Add(path);
            if (path == "/oauth/token")
                return Json("{\"access_token\":\"new\",\"refresh_token\":\"refresh\",\"expires_in\":86400}");
            if (req.Headers.Authorization!.Parameter == "old")
                return new(HttpStatusCode.Unauthorized);
            return Json("{\"id\":7,\"nickname\":\"Alex\"}");
        });
        var api = new ShikimoriService(new(handler), new("id", "secret"), store);
        Assert.Equal(7, (await api.CurrentUser()).Id);
        Assert.Equal(new[] { "/api/users/whoami", "/oauth/token", "/api/users/whoami" }, requests);
        Assert.Equal("new", (await store.Read())!.AccessToken);
    }

    [Fact]
    public async Task RateLimitIsReported()
    {
        var api = new ShikimoriService(new(new Handler(_ => new((HttpStatusCode)429))), new(), new MemoryTokens());
        var ex = await Assert.ThrowsAsync<IOException>(() => api.CurrentUser());
        Assert.Contains("Лимит", ex.Message);
    }

    [Fact]
    public async Task UpdateSendsAbsoluteEpisodeCount()
    {
        string body = "";
        var handler = new AsyncHandler(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            Assert.Equal(HttpMethod.Patch, req.Method);
            return Json("{}");
        });
        var api = new ShikimoriService(new(handler), new(), new MemoryTokens());
        await api.Update(7, new() { ShikimoriId = 1, RateId = 2, EpisodesWatched = 15, UserStatus = "watching" });
        Assert.Contains("user_rate%5Bepisodes%5D=15", body);
        Assert.DoesNotContain("increment", body);
    }

    [Fact]
    public async Task WindowsTokensAreEncryptedAndCanBeCleared()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var path = Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString(), "token");
        var store = new WindowsTokenStore(path);
        await store.Write(new("sensitive-access", "refresh", DateTimeOffset.UtcNow));
        Assert.DoesNotContain("sensitive-access", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
        Assert.Equal("sensitive-access", (await store.Read())!.AccessToken);
        await store.Clear();
        Assert.Null(await store.Read());
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(handle(r));
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => handle(r);
    }

    private sealed class MemoryTokens : ITokenStore
    {
        private OAuthToken? value = new("old", "refresh", DateTimeOffset.UtcNow);
        public Task<OAuthToken?> Read(CancellationToken ct = default) => Task.FromResult(value);
        public Task Write(OAuthToken t, CancellationToken ct = default)
        {
            value = t;
            return Task.CompletedTask;
        }

        public Task Clear(CancellationToken ct = default)
        {
            value = null;
            return Task.CompletedTask;
        }
    }
}
