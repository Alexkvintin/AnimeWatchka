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
    private const string DatabaseError = "<html><head><title>MySQL Fatal Error</title></head><body>MySQL Error!</body></html>";
    [Fact]
    public async Task DatabaseFailureWithHttp200IsReportedForReleaseAndSearch()
    {
        var provider = new AnimeVostProvider(new(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(DatabaseError) })), new AnimeVostParser());
        Assert.Contains("сбой базы данных", (await Assert.ThrowsAsync<ProviderException>(() => provider.GetEpisodes(Url))).Message);
        Assert.Contains("сбой базы данных", (await Assert.ThrowsAsync<ProviderException>(() => provider.Search("Black Lagoon"))).Message);
        var availability = await provider.CheckAvailability();
        Assert.False(availability.Available);
    }
    [Fact]
    public async Task DatabaseFailureIsNotCachedAndRecoveryNeedsNoRestart()
    {
        var requests = 0;
        var provider = new AnimeVostProvider(new(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(++requests <= 2 ? DatabaseError : Fixture("movie")) })), new AnimeVostParser());
        await Assert.ThrowsAsync<ProviderException>(() => provider.GetEpisodes(Url));
        Assert.Single(await provider.GetEpisodes(Url));
        Assert.Equal(3, requests);
    }
    [Fact]
    public async Task TransientDatabaseFailureRecoversAutomatically()
    {
        var requests = 0;
        var provider = new AnimeVostProvider(new(new Handler(r => {
            if (++requests == 1) return new(HttpStatusCode.OK) { Content = new StringContent(DatabaseError) };
            Assert.True(r.Headers.CacheControl?.NoCache);
            return new(HttpStatusCode.OK) { Content = new StringContent(Fixture("movie")) };
        })), new AnimeVostParser());
        Assert.Single(await provider.GetEpisodes(Url));
        Assert.Equal(2, requests);
    }
    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AnimeVost", name + ".html"));
    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task MissingHdAutomaticallyUsesExistingSd(int missingStatus)
    {
        var provider = new AnimeVostProvider(new(new Handler(r => {
            if(r.RequestUri!.Host=="media.example") {
                Assert.Equal("bytes=0-1023",r.Headers.Range!.ToString());
                Assert.Equal("animevost.org",r.Headers.Referrer!.Host);
                return new(r.RequestUri.AbsolutePath.Contains("/720/") ? (HttpStatusCode)missingStatus : HttpStatusCode.PartialContent);
            }
            return new(HttpStatusCode.OK) {Content=new StringContent(r.RequestUri.AbsolutePath=="/frame2.php" ? Fixture("media") : Fixture("movie"))};
        })),new AnimeVostParser());
        var source=await provider.GetPlayback(Url,Assert.Single(await provider.GetEpisodes(Url)));
        Assert.Equal("480p",Assert.Single(source.Variants!).Quality);
        Assert.DoesNotContain("/720/",source.Location);
    }
    [Fact]
    public async Task MissingAllVideoFilesDoesNotOpenABlackPlayer()
    {
        var provider=new AnimeVostProvider(new(new Handler(r=>r.RequestUri!.Host=="media.example"
            ? new(HttpStatusCode.NotFound)
            : new(HttpStatusCode.OK) {Content=new StringContent(r.RequestUri.AbsolutePath=="/frame2.php" ? Fixture("media") : Fixture("movie"))})),new AnimeVostParser());
        var episode=Assert.Single(await provider.GetEpisodes(Url));
        Assert.Contains("во всех доступных качествах",(await Assert.ThrowsAsync<ProviderException>(()=>provider.GetPlayback(Url,episode))).Message);
    }
    [Fact]
    public async Task SearchRetryPreservesQueryAndFormType()
    {
        var requests = 0;
        var provider = new AnimeVostProvider(new(new Handler(r => {
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Equal("application/x-www-form-urlencoded", r.Content!.Headers.ContentType!.MediaType);
            Assert.Contains("story=Black+Lagoon", r.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(++requests == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(Fixture("search")) };
        })), new AnimeVostParser());
        Assert.Equal(2, (await provider.Search("Black Lagoon")).Count);
        Assert.Equal(2, requests);
    }
    [Fact]
    public async Task PlayerFrameRecoversFromTemporaryGatewayFailure()
    {
        var frames = 0;
        var provider = new AnimeVostProvider(new(new Handler(r => {
            if(r.RequestUri!.AbsolutePath=="/frame2.php")
                return new(++frames == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK) { Content = new StringContent(Fixture("media")) };
            return new(HttpStatusCode.OK) { Content = new StringContent(Fixture("movie")) };
        })), new AnimeVostParser());
        var episode = Assert.Single(await provider.GetEpisodes(Url));
        Assert.Equal(PlaybackKind.DirectMedia, (await provider.GetPlayback(Url,episode)).Kind);
        Assert.Equal(2,frames);
    }
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
