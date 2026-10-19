using System.Net;
using System.Text.Json;
using AnimeApp.Models;
using AnimeApp.Providers.AniLibria;
using AnimeApp.Providers.AnimeVost;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Providers;
using AnimeApp.ViewModels;
using Xunit;

namespace AnimeApp.Tests;
public sealed class Stage5Tests
{
    [Fact]
    public void ExactYearAndRangeMatchServerSemantics()
    {
        var exact = new AnimeSearch(FromYear: 2024);
        Assert.Contains("season=2024", exact.ToShikimoriQuery());
        Assert.True(exact.Matches(new() { Year = 2024 }));
        Assert.False(exact.Matches(new() { Year = 2025 }));
        var range = new AnimeSearch(FromYear: 2022, ToYear: 2024, Season: "fall", Kind: "tv", Status: "ongoing");
        Assert.Contains("season=fall_2022,fall_2023,fall_2024", range.ToShikimoriQuery());
        Assert.True(range.Matches(new() { Year = 2023, AirMonth = 10, Kind = "tv", Status = "ongoing" }));
        Assert.False(range.Matches(new() { Year = 2023, AirMonth = 7, Kind = "tv", Status = "ongoing" }));
        Assert.Throws<ArgumentException>(() => new AnimeSearch(FromYear: 2025, ToYear: 2020).Validate());
    }

    [Fact]
    public void AutoUsesHighestQualityRegardlessOfOrder()
    {
        MediaVariant[] variants = [new("480p", "https://example.org/480"), new("1080p", "https://example.org/1080"), new("720p", "https://example.org/720")];
        Assert.Equal("1080p", QualityNegotiation.Select(variants)!.Quality);
        Assert.Equal("720p", QualityNegotiation.Select(variants, "720p")!.Quality);
        Assert.Equal("720p", QualityNegotiation.Select(variants, maximumHeight: 720)!.Quality);
        Assert.Throws<InvalidOperationException>(() => QualityNegotiation.Select(variants, maximumHeight: 240));
    }

    [Fact]
    public void MasterReadsResolutionAndRelativePaths()
    {
        var variants = QualityNegotiation.ParseMaster("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080\n1080/stream.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=1280x720\n720.m3u8", new("https://example.org/video/master.m3u8"));
        Assert.Equal(2, variants.Count);
        Assert.Equal("https://example.org/video/1080/stream.m3u8", variants[0].Location);
        Assert.Equal(3000000, variants[0].Bandwidth);
    }

    [Fact]
    public void AnimeVostScheduleOnlyAcceptsOwnExplicitLinks()
    {
        var items = AnimeVostProvider.ParseScheduleLinks("<div id='raspisMon'><a href='/tip/tv/1-test.html'>Test ~ (17:30)</a><a href='https://other.example/2.html'>Other</a></div><a href='/tip/tv/3-other.html'>Not schedule</a>");
        Assert.Single(items);
        Assert.Equal("animevost", items[0].ProviderId);
        Assert.Equal(DayOfWeek.Monday, items[0].DayOfWeek);
        Assert.NotNull(items[0].Release);
        Assert.False(items[0].Released);
    }

    [Fact]
    public void GroupingUsesIdentityAndKeepsUnknownReleasesSeparate()
    {
        ScheduleItem Item(long id, string provider, string release, bool ready) => new(new() { ShikimoriId = id, Name = "Same" }, 1, null)
        {
            ProviderId = provider,
            ProviderName = provider,
            Release = new(release, "Same"),
            Released = ready
        };
        var items = ScheduleViewModel.Group([Item(12, "a", "one", false), Item(12, "b", "two", true), Item(0, "a", "unknown1", true), Item(0, "b", "unknown2", false)]);
        Assert.Equal(3, items.Count);
        Assert.True(items[0].Released);
        Assert.Contains("a", items[0].SourcesLabel);
        Assert.Contains("b", items[0].SourcesLabel);
    }

    [Fact]
    public void AniLibertyParsesIdentityPosterAndAllOfficialQualities()
    {
        using var json = JsonDocument.Parse("""{"id":9542,"alias":"frieren","name":{"main":"Фрирен","english":"Sousou no Frieren"},"type":{"value":"TV"},"year":2023,"episodes_total":28,"shikimori":{"id":52991},"poster":{"src":"/poster.jpg"},"episodes":[{"id":"one","ordinal":1,"hls_480":"https://example.org/480.m3u8","hls_720":"https://example.org/720.m3u8","hls_1080":"https://example.org/1080.m3u8"}]}""");
        var release = AniLibriaProvider.ParseRelease(json.RootElement);
        Assert.Equal(new AnimeIdentity("shikimori", "52991"), release.MetadataIdentity);
        Assert.Equal("https://aniliberty.top/poster.jpg", release.Poster);
        Assert.Equal(new[] { "1080p", "720p", "480p" }, AniLibriaProvider.Qualities(json.RootElement.GetProperty("episodes")[0]).Select(v => v.Quality));
    }

    sealed class Http(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(response(r));
    }

    [Fact]
    public async Task AniLibertyHonorsProviderRestrictionsBeforePlayback()
    {
        var provider = new AniLibriaProvider(new(new Http(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"is_blocked_by_geo\":true}") })));
        await Assert.ThrowsAsync<ProviderException>(() => provider.GetEpisodes("1"));
        await Assert.ThrowsAsync<ProviderException>(() => provider.GetPlayback("1", new("one", 1, "")));
    }

    [Fact]
    public void AnimeVostReadsPlayerJsFullHdWithoutMislabelingFallbacks()
    {
        var parser = new AnimeVostParser();
        var variants = parser.Media("""<script>new Playerjs({"file":"[SD (480p)]https://cdn.example/123.mp4?token=keep,[HD (720р)]https://cdn.example/720/123.mp4,[FHD (1080p)]https://cdn.example/1080/123.mp4?token=keep or https://cdn.example/720/123.mp4"});</script>""", new Uri("https://animevost.org/frame5.php?play=123&old=1"));
        Assert.Equal(new[] { "1080p", "720p", "480p" }, variants.Select(v => v.Quality));
        Assert.Equal("https://cdn.example/1080/123.mp4?token=keep", variants[0].Location);
        Assert.Single(variants, v => v.Location.Contains("/720/"));
        Assert.Empty(parser.Media("""<script>new Playerjs({"file":"[FHD (1080p)]https://cdn.example/720/123.mp4"});</script>""", new Uri("https://animevost.org/")));
    }

    [Fact]
    public void AnimeVostLegacyPlayerMustBeExplicitAndSameOrigin()
    {
        var parser = new AnimeVostParser();
        var page = new Uri("https://animevost.org/frame5.php?play=123");
        Assert.Equal("https://animevost.org/frame5.php?play=123&old=1", parser.LegacyFrame("document.location.href='/frame5.php?play=123&old=1';", page));
        Assert.Null(parser.LegacyFrame("document.location.href='https://other.example/frame5.php?play=123&old=1';", page));
        Assert.Null(parser.LegacyFrame("<script></script>", page));
    }

    [Fact]
    public void RestrictedScheduleReleaseCannotAdvertiseReadyPlayback()
    {
        using var json = JsonDocument.Parse("""[{"release":{"id":1,"is_blocked_by_geo":true,"name":{"main":"Restricted"}},"published_release_episode":{"ordinal":1,"hls_720":"https://example.org/720.m3u8"}}]""");
        Assert.False(Assert.Single(AniLibriaProvider.ParseSchedule(json.RootElement)).Released);
    }

    [Fact]
    public async Task ProbeReportsPartialHtmlWithoutClaimingEpisodeSupport()
    {
        var probe = new SourceProbeService(new(new Http(_ => new(HttpStatusCode.OK) { Content = new StringContent("<title>Test</title><video src='/video.mp4'></video><script type='application/ld+json'>{}</script>", System.Text.Encoding.UTF8, "text/html") })));
        var result = await probe.Probe("https://example.org/");
        Assert.Equal(SourceSupport.Partial, result.Support);
        Assert.True(result.HtmlVideo);
        Assert.True(result.JsonLd);
        IAnimeProvider web = new GenericWebProvider(new("web", "Test", "https://example.org"), new());
        Assert.Equal(ProviderCapabilities.WebPlayback, web.Features);
        Assert.Equal("", KnownSourceAdapters.Find("animevost.org.evil.example"));
        Assert.Equal("anilibria", KnownSourceAdapters.Find("aniliberty.top"));
    }

    [Fact]
    public void TemplateRejectsCodeCrossSiteAndInvalidSelectors()
    {
        var d = new TemplateDefinition("template-" + new string ('a', 32), "Test", "https://example.org", "https://example.org/search?q={query}", "a.release", "a.episode", "img.poster", "video");
        d.Validate();
        Assert.Throws<ArgumentException>(() => (d with { SearchTemplate = "https://other.org/?q={query}" }).Validate());
        Assert.Throws<ArgumentException>(() => (d with { VideoSelector = "[" }).Validate());
        Assert.Throws<ArgumentException>(() => (d with { BaseUrl = "javascript:alert(1)" }).Validate());
    }
}
