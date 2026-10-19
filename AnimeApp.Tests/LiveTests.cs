using System.Net;
using System.Text;
using AnimeApp.Services.Shikimori;
using AnimeApp.Providers.AnimeVost;
using Xunit;

namespace AnimeApp.Tests;
public sealed class LiveTests
{
    [LiveFact, Trait("Category", "Live")]
    public async Task PublicShikimoriAndAnimeVostHttpWork()
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        var api = new ShikimoriService(http, new(), new NoTokens());
        var results = await api.Search("Frieren");
        Assert.Contains(results, a => a.ShikimoriId == 52991);
        var details = await api.Details(52991);
        Assert.Equal(28, details.EpisodesTotal);
        Assert.False(string.IsNullOrWhiteSpace(details.Description));
        var provider = new AnimeVostProvider(http, new AnimeVostParser());
        Assert.True((await provider.CheckAvailability()).Available);
        Assert.NotEmpty(await provider.Search("Naruto"));
        const string release = "https://animevost.org/tip/tv/3044-sousou-no-frieren.html";
        Assert.Equal(28, (await provider.GetRelease(release)).EpisodesAvailable);
        var episodes = await provider.GetEpisodes(release);
        Assert.Equal(28, episodes.Count);
        var source = await provider.GetPlayback(release, episodes[0]);
        Assert.Equal(AnimeApp.Services.Playback.PlaybackKind.DirectMedia, source.Kind);
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Location);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
    }

    [LiveFact, Trait("Category", "Live")]
    public async Task PublicCalendarRelationsAndSourceProbeWork()
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        var api = new ShikimoriService(http, new(), new NoTokens());
        var calendar = await api.Schedule();
        Assert.NotEmpty(calendar);
        Assert.Contains(calendar, i => i.AirDateTime is not null);
        Assert.Contains(await api.Relations(52991), r => r.Relation == "sequel" && r.IsSeason);
        var probe = new AnimeApp.Services.Providers.SourceProbeService(http);
        Assert.Equal(AnimeApp.Services.Providers.SourceSupport.Known, (await probe.Probe("https://animevost.org")).Support);
        Assert.Equal(AnimeApp.Services.Providers.SourceSupport.Web, (await probe.Probe("https://shikimori.io")).Support);
        Assert.Equal(AnimeApp.Services.Providers.SourceSupport.AdapterRequired, (await probe.Probe("https://shikimori.io/api/animes/52991")).Support);
    }

    [LiveFact, Trait("Category", "Live")]
    public async Task OfficialAniLibertyAndServerYearFiltersWork()
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        var api = new ShikimoriService(http, new(), new NoTokens());
        var exact = await api.Search(new AnimeApp.Services.Providers.AnimeSearch("Frieren", 2023, 2023, "tv"));
        Assert.Contains(exact, a => a.ShikimoriId == 52991);
        Assert.All(exact, a => Assert.Equal(2023, a.Year));
        var range = await api.Search(new AnimeApp.Services.Providers.AnimeSearch(FromYear: 2022, ToYear: 2024, Season: "fall", Kind: "tv"));
        // Shikimori classifies Frieren (September premiere) as fall; season is not a strict month quarter.
        Assert.Contains(range, a => a.ShikimoriId == 52991);
        Assert.NotEmpty(range);
        Assert.All(range, a =>
        {
            Assert.InRange(a.Year ?? 0, 2022, 2024);
            Assert.Equal("tv", a.Kind);
        });
        var provider = new AnimeApp.Providers.AniLibria.AniLibriaProvider(http);
        var release = (await provider.Search("Sousou no Frieren")).First(r => r.MetadataIdentity?.Id == "52991");
        var episodes = await provider.GetEpisodes(release.Id);
        Assert.Equal(28, episodes.Count);
        var source = await provider.GetPlayback(release.Id, episodes[0]);
        Assert.Contains(source.Variants!, v => v.Height == 1080);
        var schedule = await provider.Schedule();
        Assert.NotEmpty(schedule);
        Assert.All(schedule, i =>
        {
            Assert.Equal("anilibria", i.ProviderId);
            Assert.NotNull(i.Release);
        });
    }

    private sealed class NoTokens : ITokenStore
    {
        public Task<OAuthToken?> Read(CancellationToken ct = default) => Task.FromResult<OAuthToken?>(null);
        public Task Write(OAuthToken t, CancellationToken ct = default) => Task.CompletedTask;
        public Task Clear(CancellationToken ct = default) => Task.CompletedTask;
    }
}

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ANIMEAPP_LIVE_TESTS") != "1")
            Skip = "Для сетевых проверок задайте ANIMEAPP_LIVE_TESTS=1.";
    }
}
