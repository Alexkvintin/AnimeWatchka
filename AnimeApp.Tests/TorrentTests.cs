using System.Net;
using AnimeApp.Services.Torrents;
using Xunit;
namespace AnimeApp.Tests;
public class TorrentTests
{
    [Fact]
    public void SearchNamesPreferOriginalAndEnglishAndRemoveDuplicates() {
        var names = TorrentProvider.SearchNames(new() { Name="Original", EnglishName="English", JapaneseName="Original", RussianName="Русское" });
        Assert.Equal(new[] { "Original", "English", "Русское" }, names);
    }
    [Theory]
    [InlineData("Show [RUS SUB]", true)]
    [InlineData("Show [Sub Rus]", true)]
    [InlineData("Show русские субтитры", true)]
    [InlineData("Show Russian Dub", false)]
    [InlineData("Show Multi Subs", false)]
    [InlineData("Show [ENG SUB]", false)]
    public void RussianSubtitlesAreNotConfusedWithDubOrUnknown(string title, bool expected) =>
        Assert.Equal(expected, TorrentProvider.HasRussianSubtitles(new(title,"magnet:?")));
    [Fact]
    public void ExplicitRussianSubtitleMetadataIsAccepted() =>
        Assert.True(TorrentProvider.HasRussianSubtitles(new("Show", "magnet:?", SubtitleLanguages: ["en", "ru"])));
    [Fact]
    public async Task MissingCatalogIsConfigurationErrorNotEmptySearch() {
        var db=new AnimeApp.Services.Database.DatabaseService(Path.Combine(Path.GetTempPath(),"AnimeAppTests",Guid.NewGuid().ToString(),"empty.db")); await db.Initialize();
        await using var session=new TorrentSession();
        var provider=new TorrentProvider(new(db),session,new SearchHandler());
        var error=await Assert.ThrowsAsync<IOException>(()=>provider.Find("Show",default));
        Assert.Contains("Нет включённых каталогов", error.Message);
    }
    [Theory]
    [InlineData("Show - 02.mkv","Show - 02.ru.ass",2,true)]
    [InlineData("Show - 02.mkv","01.ru.ass",2,false)]
    [InlineData("Movie.mkv","English.ass",1,true)]
    [InlineData("Show - 02.mkv","02.ass",2,true)]
    public void ExternalSubtitlesBelongToSelectedEpisode(string video,string subtitle,int count,bool expected) => Assert.Equal(expected,TorrentSession.MatchesSubtitle(video,subtitle,count));
    [Fact]
    public async Task BackgroundContinuationUsesSavedEpisodesWithoutOpeningTorrent() {
        var db=new AnimeApp.Services.Database.DatabaseService(Path.Combine(Path.GetTempPath(),"AnimeAppTests",Guid.NewGuid().ToString(),"offline.db")); await db.Initialize();
        var repo=new AnimeApp.Services.Database.LibraryRepository(db);
        await repo.SaveMapping(new(42,"torrent-subtitles","cached"));
        await repo.SaveSetting("torrent-episodes-cached",new[]{new AnimeApp.Services.Providers.ProviderEpisode("Show - 02.mkv",2,"Episode 2","cached")});
        await using var session=new TorrentSession(); var provider=new TorrentProvider(repo,session);
        var result=await new AnimeApp.Services.ContinuationService(repo).Find(new(){ShikimoriId=42,EpisodesWatched=1},[],[provider]);
        Assert.True(result.Available); Assert.Equal(2,result.Episode!.Number);
        Assert.Equal("Торрент остановлен",session.Status);
    }
    private sealed class SearchHandler : HttpMessageHandler {
        public string Query = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            Query=request.RequestUri!.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent("""[{"title":"Demo - 01","magnet":"magnet:?xt=urn:btih:0123456789012345678901234567890123456789","quality":"1080p","size":1234,"seeds":4,"episodes":"1"},{"title":"Bad","magnet":"javascript:alert(1)"}]""") });
        }
    }
    [Fact]
    public async Task SearchUsesEnabledUserSourcesAndFiltersInvalidLinks() {
        var db=new AnimeApp.Services.Database.DatabaseService(Path.Combine(Path.GetTempPath(),"AnimeAppTests",Guid.NewGuid().ToString(),"torrent.db")); await db.Initialize();
        var repository=new AnimeApp.Services.Database.LibraryRepository(db);
        await repository.SaveSetting("torrent-search-sources",new List<TorrentSearchSource>{new("Example","https://example.org/search?q={query}"),new("Disabled","https://invalid.example/search?q={query}",false)});
        await using var session=new TorrentSession(); var handler=new SearchHandler();
        var provider=new TorrentProvider(repository,session,handler);
        var result=Assert.Single(await provider.Find("Demo & season",default));
        Assert.Equal(4,result.Seeds); Assert.Equal("1080p",result.Quality); Assert.Contains("%26",handler.Query);
    }
    [Fact]
    public void ImportedManifestIsDataOnlyAndDisabledUntilSelected() {
        var sources=TorrentProvider.ParseManifest("""{"version":1,"sources":[{"name":"My source","searchUrl":"https://example.org/search?q={query}","enabled":true}]}""");
        Assert.False(Assert.Single(sources).Enabled);
        Assert.Throws<System.Text.Json.JsonException>(()=>TorrentProvider.ParseManifest("export default class Source {}"));
        Assert.Throws<IOException>(()=>TorrentProvider.ParseManifest("""{"version":2,"sources":[]}"""));
    }
    [Theory]
    [InlineData("bytes=20-29",100,20,29)]
    [InlineData("bytes=-10",100,90,99)]
    [InlineData("bytes=80-",100,80,99)]
    [InlineData("bytes=0-999",100,0,99)]
    public void Ranges(string value,long length,long start,long end) { Assert.True(TorrentHttpStream.TryRange(value,length,out var a,out var b)); Assert.Equal(start,a); Assert.Equal(end,b); }
    [Theory]
    [InlineData("bytes=100-")][InlineData("bytes=20-10")][InlineData("bytes=0-1,4-5")][InlineData("bytes=-0")][InlineData("bytes=abc-")]
    public void InvalidRanges(string value) => Assert.False(TorrentHttpStream.TryRange(value,100,out _,out _));
    [Fact]
    public async Task LoopbackServerRequiresCapabilityAndSupportsSeeking()
    {
        var data=Enumerable.Range(0,200).Select(i=>(byte)i).ToArray();
        await using var server=new TorrentHttpStream(new MemoryStream(data));
        using var client=new HttpClient();
        using var get=new HttpRequestMessage(HttpMethod.Get,server.Url); get.Headers.Range=new(120,139);
        using var result=await client.SendAsync(get);
        Assert.Equal(HttpStatusCode.PartialContent,result.StatusCode);
        Assert.Equal(data[120..140],await result.Content.ReadAsByteArrayAsync());
        using var head=await client.SendAsync(new(HttpMethod.Head,server.Url)); Assert.Equal(200,head.Content.Headers.ContentLength);
        using var wrong=await client.GetAsync(server.Url+"bad"); Assert.Equal(HttpStatusCode.Forbidden,wrong.StatusCode);
        using var origin=new HttpRequestMessage(HttpMethod.Get,server.Url); origin.Headers.Add("Origin","https://example.com");
        using var rejected=await client.SendAsync(origin); Assert.Equal(HttpStatusCode.Forbidden,rejected.StatusCode);
    }
    [Theory]
    [InlineData("Show - 12 [1080p].mkv",12)] [InlineData("Show S02E03.mkv",3)] [InlineData("07 - Title.mkv",7)] [InlineData("Movie (1998) 1080p.mkv",0)]
    public void EpisodeNumberDoesNotConfuseResolutionOrYear(string path,int number) => Assert.Equal(number,TorrentProvider.EpisodeNumber(path));
    [Theory]
    [InlineData("http://site/search?q={query}")][InlineData("https://localhost/search?q={query}")][InlineData("https://site/extension.js")]
    public void InvalidSearchSource(string url) => Assert.Throws<IOException>(()=>TorrentProvider.ValidateSource(new("Test",url)));
}
