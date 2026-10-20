using System.Net;
using AnimeApp.Models;
using AnimeApp.Services.Database;
using AnimeApp.Services.Shikimori;
using Xunit;

namespace AnimeApp.Tests;
public sealed class ContentVisibilityTests
{
    [Fact]
    public async Task DefaultHidesAdultAndUnknownAndSettingCanRestoreThem()
    {
        var api = new ShikimoriService(new(new Handler()), new(), new Tokens());
        var items = new[] { new Anime { ShikimoriId = 1 }, new Anime { ShikimoriId = 2 }, new Anime { ShikimoriId = 3 }, new Anime { ShikimoriId = 4 }, new Anime() };
        Assert.False(api.ShowAdultContent);
        Assert.Equal(1, Assert.Single(await api.FilterContent(items)).ShikimoriId);
        api.ShowAdultContent = true;
        Assert.Equal(5, (await api.FilterContent(items)).Count);
        api.ShowAdultContent = false;
        Assert.Single(await api.FilterContent(items));
    }
    [Fact]
    public async Task VerifiedRatingsSurviveRestartWithoutNetwork()
    {
        var db = new DatabaseService(Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString(), "db"));
        await db.Initialize();
        var repo = new LibraryRepository(db);
        var api = new ShikimoriService(new(new Handler()), new(), new Tokens(), repository: repo);
        Assert.Single(await api.FilterContent([new Anime { ShikimoriId = 1 }, new Anime { ShikimoriId = 2 }]));
        var offline = new ShikimoriService(new(new Handler(true)), new(), new Tokens(), repository: repo);
        Assert.Equal(1, Assert.Single(await offline.FilterContent([new Anime { ShikimoriId = 1 }, new Anime { ShikimoriId = 2 }])).ShikimoriId);
    }
    [Fact]
    public async Task SearchWithMissingRatingUsesServerFilterWithoutGraphql()
    {
        var api = new ShikimoriService(new(new Handler()), new(), new Tokens());
        Assert.Equal(1, Assert.Single(await api.Search("test")).ShikimoriId);
        Assert.Equal(1, Assert.Single(await api.FilterContent([new Anime { ShikimoriId = 1 }])).ShikimoriId);
    }
    [Fact]
    public async Task LegacyFalseIsRecheckedInsteadOfPermanentlyHidingOrdinaryAnime()
    {
        var db = new DatabaseService(Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString(), "db"));
        await db.Initialize();
        var repo = new LibraryRepository(db);
        await repo.SaveSetting("content-ratings", new Dictionary<long, bool> { [1] = false, [2] = false });
        var api = new ShikimoriService(new(new Handler()), new(), new Tokens(), repository: repo);
        Assert.Equal(1, Assert.Single(await api.FilterContent([new Anime { ShikimoriId = 1 }, new Anime { ShikimoriId = 2 }])).ShikimoriId);
    }
    [Fact]
    public async Task ServerFailureIsAnErrorNotAnEmptyLibraryOrPersistentBan()
    {
        var handler = new RecoveringHandler();
        var api = new ShikimoriService(new(handler), new(), new Tokens());
        await Assert.ThrowsAsync<ShikimoriApiException>(() => api.FilterContent([new Anime { ShikimoriId = 1 }]));
        handler.Fail = false;
        Assert.Single(await api.FilterContent([new Anime { ShikimoriId = 1 }]));
    }
    sealed class RecoveringHandler : HttpMessageHandler
    {
        public bool Fail = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(Fail ? "" : "[{\"id\":1}]") });
    }
    [Theory]
    [InlineData("g", true)] [InlineData("pg", true)] [InlineData("pg_13", true)] [InlineData("r", true)]
    [InlineData("r_plus", false)] [InlineData("rx", false)] [InlineData("", false)]
    public void RatingsAreExplicit(string rating, bool safe) => Assert.Equal(safe, ShikimoriService.SafeRating(rating));
    sealed class Handler(bool offline = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (offline) throw new InvalidOperationException("Unexpected network request");
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("censored=true", request.RequestUri!.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""[{"id":1,"name":"Ordinary anime"}]""") });
        }
    }
    sealed class Tokens : ITokenStore
    {
        public Task<OAuthToken?> Read(CancellationToken ct = default) => Task.FromResult<OAuthToken?>(null);
        public Task Write(OAuthToken token, CancellationToken ct = default) => Task.CompletedTask;
        public Task Clear(CancellationToken ct = default) => Task.CompletedTask;
    }
}
