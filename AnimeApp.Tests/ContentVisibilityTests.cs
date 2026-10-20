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
    [Theory]
    [InlineData("g", true)] [InlineData("pg", true)] [InlineData("pg_13", true)] [InlineData("r", true)]
    [InlineData("r_plus", false)] [InlineData("rx", false)] [InlineData("", false)]
    public void RatingsAreExplicit(string rating, bool safe) => Assert.Equal(safe, ShikimoriService.SafeRating(rating));
    sealed class Handler(bool offline = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (offline) throw new InvalidOperationException("Unexpected network request");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":{"animes":[{"id":"1","rating":"pg_13"},{"id":"2","rating":"rx"},{"id":"3","rating":"r_plus"}]}}""") });
        }
    }
    sealed class Tokens : ITokenStore
    {
        public Task<OAuthToken?> Read(CancellationToken ct = default) => Task.FromResult<OAuthToken?>(null);
        public Task Write(OAuthToken token, CancellationToken ct = default) => Task.CompletedTask;
        public Task Clear(CancellationToken ct = default) => Task.CompletedTask;
    }
}
