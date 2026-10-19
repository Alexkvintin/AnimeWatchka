using AnimeApp.Providers.AnimeVost;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Playback;
using AnimeApp.Models;
using Xunit;
using System.Net;

namespace AnimeApp.Tests;
public sealed class AnimeVostTests
{
    private const string Url = "https://animevost.org/tip/tv/3044-sousou-no-frieren.html";
    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AnimeVost", name + ".html"));
    [Fact]
    public void SearchFindsTwoSeasons()
    {
        var result = new AnimeVostParser().SearchResults(Fixture("search"));
        Assert.Equal(2, result.Count);
        Assert.Equal(28, result[0].Total);
    }

    [Fact]
    public void ReleaseAndActualEpisodes()
    {
        var r = new AnimeVostParser().Release(Fixture("frieren"), Url);
        Assert.Equal(2023, r.Release.Year);
        Assert.Equal("tv", r.Release.Kind);
        Assert.Equal(28, r.Episodes.Count);
        Assert.Equal("1055923300", r.Episodes[0].Id);
        Assert.Equal(28, r.Episodes[^1].Number);
    }

    [Fact]
    public void MovieIsOneActualEpisode()
    {
        var r = new AnimeVostParser().Release(Fixture("movie"), "https://animevost.org/tip/polnometrazhnyy-film/movie.html");
        var e = Assert.Single(r.Episodes);
        Assert.Equal("Фильм", e.Title);
        Assert.Equal(1, e.Number);
        Assert.Equal("movie", r.Release.Kind);
    }

    [Fact]
    public void OngoingDoesNotInventEpisodes()
    {
        var html = Fixture("frieren");
        html = System.Text.RegularExpressions.Regex.Replace(html, @"var data = \{[^}]+\};", "var data = {\"1 серия\":\"11\",\"2 серия\":\"22\",};");
        Assert.Equal(2, new AnimeVostParser().Release(html, Url).Episodes.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><h1>Broken</h1><script>var data={oops};</script>")]
    [InlineData("<h1>Release</h1>")]
    public void MalformedOrMissingSeriesHasReadableError(string html) => Assert.Throws<ProviderException>(() => new AnimeVostParser().Release(html, Url));
    [Fact]
    public void MediaAndIframeAreParsedWithoutExecutingJs()
    {
        var p = new AnimeVostParser();
        Assert.Equal("https://animevost.org/frame5.php?play=1055923300", p.Frame(Fixture("frame"), new(Url)));
        var variants = p.Media(Fixture("media"), new(Url));
        Assert.Equal(2, variants.Count);
        Assert.Equal("720p", variants[0].Quality);
    }

    [Fact]
    public async Task PlaybackFollowsRealFrameChain()
    {
        var h = new Handler(r => new(HttpStatusCode.OK) { Content = new StringContent(r.RequestUri!.AbsolutePath switch
        {
            "/frame2.php" => Fixture("frame"),
            "/frame5.php" => Fixture("media"),
            _ => Fixture("frieren")}) });
        var p = new AnimeVostProvider(new(h), new AnimeVostParser());
        var episodes = await p.GetEpisodes(Url);
        var playback = await p.GetPlayback(Url, episodes[0]);
        Assert.Equal(PlaybackKind.DirectMedia, playback.Kind);
        Assert.Equal(2, playback.Variants!.Count);
    }

    [Fact]
    public async Task WebOnlyPlayerRemainsWebPage()
    {
        var h = new Handler(r => new(HttpStatusCode.OK) { Content = new StringContent(r.RequestUri!.AbsolutePath.Contains("frame") ? "<html><video></video></html>" : Fixture("frieren")) });
        var p = new AnimeVostProvider(new(h), new AnimeVostParser());
        Assert.Equal(PlaybackKind.WebPage, (await p.GetPlayback(Url, (await p.GetEpisodes(Url))[0])).Kind);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task HttpErrorsAreReadable(int status)
    {
        var p = new AnimeVostProvider(new(new Handler(_ => new((HttpStatusCode)status))), new AnimeVostParser());
        await Assert.ThrowsAsync<ProviderException>(() => p.GetRelease(Url));
    }

    [Fact]
    public async Task TimeoutAndEmptyResponse()
    {
        var timeout = new AnimeVostProvider(new(new Handler(_ => throw new TaskCanceledException())), new AnimeVostParser());
        Assert.Contains("вовремя", (await Assert.ThrowsAsync<ProviderException>(() => timeout.GetRelease(Url))).Message);
        var empty = new AnimeVostProvider(new(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("") })), new AnimeVostParser());
        Assert.Contains("пустой", (await Assert.ThrowsAsync<ProviderException>(() => empty.GetRelease(Url))).Message);
    }

    [Fact]
    public void MatchingPenalizesWrongSeasonYearAndType()
    {
        var a = new Anime
        {
            ShikimoriId = 52991,
            Name = "Sousou no Frieren",
            RussianName = "Провожающая в последний путь Фрирен",
            Year = 2023,
            Kind = "tv",
            EpisodesTotal = 28
        };
        var m = new ReleaseMatcher();
        Assert.True(m.Rank(a, new("a", a.RussianName + " / " + a.Name, Year: 2023, Kind: "tv", EpisodesTotal: 28)).Score >= 0.85);
        Assert.True(m.Rank(a, new("b", a.Name + " 2nd Season", Year: 2026, Kind: "movie", EpisodesTotal: 10)).Score < 0.6);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(handle(r));
    }
}
