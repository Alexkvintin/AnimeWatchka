using System.Net;
using System.Text.Json;
using AnimeApp.Services.Playback;
using Xunit;

namespace AnimeApp.Tests;

public sealed class AutomaticSubtitleTests
{
    private sealed class Handler(Func<Uri, string> response) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request.RequestUri)) });
        }
    }
    [Fact]
    public async Task ExactIdentityAndEpisodeKindExcludeWrongSeasonAndOpening()
    {
        var handler = new Handler(uri => uri.AbsolutePath switch
        {
            "/api/series" => """{"data":[{"id":10,"myAnimeListId":1,"type":"tv"},{"id":20,"myAnimeListId":2,"type":"tv"}]}""",
            "/api/episodes" => """{"data":[{"id":9,"episodeInt":1,"episodeType":"opening"},{"id":11,"episodeInt":1,"episodeType":"tv"}]}""",
            _ => """{"data":[{"id":100,"seriesId":10,"episodeId":11,"isActive":1,"typeKind":"sub","typeLang":"ru","authorsSummary":"Team","duration":1400}]}"""
        });
        var result = await new AutomaticSubtitles(new(handler)).Find(1, "Example", 1);
        Assert.Equal(100, Assert.Single(result).Id);
        Assert.Contains(handler.Paths, p => p.Contains("episodeId=11"));
        Assert.DoesNotContain(handler.Paths, p => p.Contains("episodeId=9"));
    }
    [Theory]
    [InlineData("{\"data\":[{\"id\":10,\"myAnimeListId\":2,\"type\":\"tv\"}]}")]
    [InlineData("{\"data\":[{\"id\":10,\"myAnimeListId\":1},{\"id\":20,\"myAnimeListId\":1}]}")]
    public async Task MissingOrAmbiguousIdentityDoesNotGuess(string series)
    {
        var handler = new Handler(_ => series);
        Assert.Empty(await new AutomaticSubtitles(new(handler)).Find(1, "Example", 1));
        Assert.Single(handler.Paths);
    }
    [Fact]
    public void OnlyActiveRussianAndUkrainianSubtitlesForExactEpisode()
    {
        using var doc = JsonDocument.Parse("""
        [{"id":1,"seriesId":10,"episodeId":11,"isActive":1,"typeKind":"sub","typeLang":"ru"},
        {"id":2,"seriesId":10,"episodeId":11,"isActive":1,"typeKind":"sub","typeLang":"uk"},
        {"id":3,"seriesId":10,"episodeId":11,"isActive":1,"typeKind":"voice","typeLang":"ru"},
        {"id":4,"seriesId":10,"episodeId":11,"isActive":0,"typeKind":"sub","typeLang":"ru"},
        {"id":5,"seriesId":10,"episodeId":12,"isActive":1,"typeKind":"sub","typeLang":"ru"},
        {"id":6,"seriesId":10,"episodeId":11,"isActive":1,"typeKind":"sub","typeLang":"en"}]
        """);
        Assert.Equal(new long[] { 1, 2 }, AutomaticSubtitles.Parse(doc.RootElement, 10, 11).Select(t => t.Id));
    }
    [Fact]
    public async Task LoginHtmlIsNotLoadedAsSubtitles()
    {
        var service = new AutomaticSubtitles(new(new Handler(_ => "<html>Login</html>")));
        await Assert.ThrowsAsync<IOException>(() => service.Download(new(1, "ru", "Team", 0)));
    }
    [Fact]
    public async Task UnknownLocalAnimeDoesNotContactCatalog()
    {
        var handler = new Handler(_ => throw new Exception("Unexpected request"));
        Assert.Empty(await new AutomaticSubtitles(new(handler)).Find(0, "Local", 1));
        Assert.Empty(handler.Paths);
    }
}
