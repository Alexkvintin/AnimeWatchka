using AnimeApp.Providers.Uakino;
using AnimeApp.Services.Providers;
using Xunit;

namespace AnimeApp.Tests;
public sealed class UakinoTests
{
    private const string Release = "https://uakino.best/animeukr/anime-series/32158-chitose.html";
    [Fact]
    public void SearchExcludesMoviesOutsideAnimeAndExternalLinks()
    {
        var results = UakinoProvider.ParseSearch("""
            <div id="dle-content">
            <a class="movie-title" href="/animeukr/anime-series/32158-chitose.html">Чітосе</a>
            <a class="movie-title" href="/animeukr/anime-series/32158-chitose.html">Duplicate</a>
            <a class="movie-title" href="/filmy/test.html">Film</a>
            <a class="movie-title" href="https://example.org/animeukr/test.html">External</a></div>
            """);
        Assert.Equal(Release, Assert.Single(results).Id);
    }
    [Fact]
    public void PlaylistRetainsVoicesAndIgnoresAdsAndInvalidEpisodes()
    {
        var episodes = UakinoProvider.ParseEpisodes(Release, """
            <iframe src="https://advert.example/video"></iframe>
            <ul class="playlists-videos">
            <li data-file="//ashdi.vip/vod/2" data-voice="Субтитри Mogi">Серія 1</li>
            <li data-file="//ashdi.vip/vod/1" data-voice="ShiWa">Серія 1</li>
            <li data-file="//ashdi.vip/vod/1" data-voice="ShiWa">Серія 1</li>
            <li data-file="javascript:alert(1)">Серія 2</li>
            <li data-file="//ashdi.vip/vod/3">Реклама</li></ul>
            """);
        Assert.Equal(2, episodes.Count);
        Assert.Equal("https://ashdi.vip/vod/1", episodes[0].PageUrl);
        Assert.Contains("ShiWa", episodes[0].Title);
        Assert.Contains("Субтитри", episodes[1].Title);
    }
    [Fact]
    public async Task RejectsForeignReleaseBeforeLoading()
    {
        var provider = new UakinoProvider((_, _, _) => throw new Exception("Must not load"));
        await Assert.ThrowsAsync<ProviderException>(() => provider.GetRelease("https://example.org/film.html"));
    }
    [Fact]
    public async Task PlaybackMustBelongToCurrentPlaylist()
    {
        var provider = new UakinoProvider((_, _, _) => Task.FromResult("<ul class='playlists-videos'><li data-file='//ashdi.vip/vod/1'>Серія 1</li></ul>"));
        await Assert.ThrowsAsync<ProviderException>(() => provider.GetPlayback(Release, new("other", 1, "other")));
        var episode = Assert.Single(await provider.GetEpisodes(Release));
        Assert.Equal("https://ashdi.vip/vod/1", (await provider.GetPlayback(Release, episode)).Location);
    }
    [Fact]
    public void ReleaseIncludesOriginalNameForMatchingAndAbsolutePoster()
    {
        var release = UakinoProvider.ParseRelease(Release, """
            <h1>Чітосе 1 сезон</h1><div class="origintitle">Chitose-kun wa Ramune Bin no Naka 1 season</div>
            <a href="/find/year/2025">2025</a><meta property="og:image" content="/uploads/poster.jpg">
            """);
        Assert.Contains("Chitose-kun wa Ramune Bin no Naka", release.Title);
        Assert.Equal(2025, release.Year);
        Assert.Equal("https://uakino.best/uploads/poster.jpg", release.Poster);
    }
}
