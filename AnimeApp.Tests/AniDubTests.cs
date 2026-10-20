using System.Net;
using AnimeApp.Providers.AniDub;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Providers;
using Xunit;

namespace AnimeApp.Tests;
public sealed class AniDubTests
{
    const string Release = "https://online.anidub.com/123-test.html";
    const string Page = "https://player26.ladonyvesna2005.info/index.php?v=/s12/abcd&playlist";
    const string Playlist = """window.PLAYER_CONTEXT = {"episodes":[{"link":"/s12/abcd","label":"1 серия"},{"link":"/s11/ef01","label":"2 серия"},{"link":"https://example.com","label":"3 серия"}]};""";
    [Fact]
    public void ParsesCatalogAndRejectsForeignReleaseLinks()
    {
        var html = $"""
        <a class="th-in" href="{Release}"><div class="th-title">Тест</div><div class="th-subtitle">1998</div><img src="/poster.jpg"></a>
        <a class="th-in" href="https://example.com/test.html"><div class="th-title">Реклама</div></a>
        """;
        var item = Assert.Single(AniDubProvider.ParseSearch(html));
        Assert.Equal(1998, item.Year);
        Assert.Equal("https://online.anidub.com/poster.jpg", item.Poster);
        var episodes = AniDubProvider.ParsePlaylist(Playlist, Release, Page);
        Assert.Equal(2, episodes.Count);
        Assert.Equal(2, episodes[1].Number);
        Assert.Empty(AniDubProvider.ParsePlaylist("window.PLAYER_CONTEXT = {invalid};", Release, Page));
    }
    [Fact]
    public async Task OpensSelectedEpisodeDirectlyAndRejectsUnlistedEpisode()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".html")) return $"<iframe id='playeriframe' src='{Page}'></iframe>";
            if (request.RequestUri.AbsolutePath == "/index.php") return Playlist;
            Assert.Equal("/vid.php", request.RequestUri.AbsolutePath);
            Assert.Contains("ef01", request.RequestUri.Query);
            Assert.Equal(Page, request.Headers.Referrer!.ToString());
            return "#EXTM3U\n#EXTINF:10,\nsegment.ts\n#EXT-X-ENDLIST";
        }));
        var provider = new AniDubProvider(http);
        var episodes = await provider.GetEpisodes(Release);
        var source = await provider.GetPlayback(Release, episodes[1]);
        Assert.Equal(PlaybackKind.DirectMedia, source.Kind);
        Assert.Contains("ef01", source.Location);
        await Assert.ThrowsAsync<ProviderException>(() => provider.GetPlayback(Release, episodes[0] with { Id = "/s1/ffff" }));
        await Assert.ThrowsAsync<ProviderException>(() => provider.GetEpisodes("https://example.com/test.html"));
    }
    [Fact]
    public async Task OldUnsupportedHostHasActionableErrorInsteadOfWebPlayer()
    {
        using var http = new HttpClient(new Handler(_ => "<span data='https://video.sibnet.ru/shell.php?videoid=1'>Серия 1</span>"));
        var provider = new AniDubProvider(http);
        var episodes = await provider.GetEpisodes(Release);
        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.GetPlayback(Release, episodes[0]));
        Assert.Contains("Sibnet", error.Message);
    }
    sealed class Handler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request)) });
    }
}
