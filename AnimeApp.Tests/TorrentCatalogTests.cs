using AnimeApp.Services.Torrents;
using System.Text;
using Xunit;
namespace AnimeApp.Tests;
public class TorrentCatalogTests {
    [Fact] public void StreamingRankingAvoidsHugeFilesWithOnlySlightlyMoreSeeds() {
        var small=new TorrentSearchResult("small","magnet:?",Size:500_000_000,Seeds:40,Episodes:"12");
        var large=small with {Size=8_000_000_000,Seeds=50};
        Assert.True(TorrentProvider.StreamingScore(small)>TorrentProvider.StreamingScore(large));
    }
    private sealed class EmptySearchHandler : HttpMessageHandler {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            Calls++; return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("[]")});
        }
    }
    [Fact] public async Task FailedCandidatesTriggerAutomaticSearchWithoutRequestingManualSetup() {
        var folder=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString()); Directory.CreateDirectory(folder);
        var db=new AnimeApp.Services.Database.DatabaseService(Path.Combine(folder,"test.db")); await db.Initialize();
        var repo=new AnimeApp.Services.Database.LibraryRepository(db);
        await repo.SaveSetting("torrent-search-sources",new[]{new TorrentSearchSource("test","https://example.org/?q={query}")});
        var handler=new EmptySearchHandler(); await using var session=new TorrentSession();
        var provider=new TorrentProvider(repo,session,handler);
        var release=await provider.SaveCatalog("test","Show",[new("Show - 01","magnet:?",Episodes:"01",TorrentUrl:"http://invalid.example/test")],default,12,["Show"]);
        var episode=Assert.Single(await provider.GetEpisodes(release.Id));
        var error=await Assert.ThrowsAsync<IOException>(()=>provider.GetPlayback(release.Id,episode));
        Assert.True(handler.Calls>0);
        Assert.DoesNotContain("Дополнительно",error.Message);
    }
    [Theory]
    [InlineData("Show - 720p.mkv",0)]
    [InlineData("[757abc01] Show - 12 [1080p].mkv",12)]
    [InlineData("[Group] Show [757abc01].mkv",0)]
    [InlineData("Show S01E12.mkv",12)]
    public void FileEpisodeIgnoresTechnicalNumbers(string name,int expected) => Assert.Equal(expected,TorrentProvider.EpisodeNumber(name));
    [Fact] public void CatalogCannotExceedKnownSeasonLength() {
        var episodes=TorrentProvider.CatalogEpisodes("catalog-test",[new("bad","magnet:?",Episodes:"01-720"),new("bad2","magnet:?",Episodes:"757")],12);
        Assert.Equal(Enumerable.Range(1,12),episodes.Select(e=>e.Number));
    }
    [Fact] public void ResolutionFollowingEpisodeIsNotAnEpisodeRange() {
        var xml="<rss><channel><item><title>Black Lagoon - S01E06 - 1080p WEB</title><infoHash>0123456789012345678901234567890123456789</infoHash></item></channel></rss>";
        var release=Assert.Single(TorrentCatalogFormats.Parse("nyaa",Encoding.UTF8.GetBytes(xml)));
        Assert.True(TorrentCatalogFormats.CoversEpisode(release,6));
        Assert.False(TorrentCatalogFormats.CoversEpisode(release,12));
    }
    [Theory]
    [InlineData("[Erai-raws] Black Lagoon - The Second Barrage - 01 ~ 12 [1080p]",false)]
    [InlineData("[Erai-raws] Black Lagoon - 01 ~ 12 [1080p]",true)]
    [InlineData("Black.Lagoon.S01.1080p.Blu-Ray.x265-iAHD",true)]
    public void FranchiseNameDoesNotMatchNamedSequel(string title,bool expected) => Assert.Equal(expected,TorrentCatalogFormats.MatchesTitle(title,["Black Lagoon"]));
    [Fact] public void SpacedBatchRangeIncludesLastEpisode() {
        var xml="<rss><channel><item><title>[Group] Black Lagoon - 01 ~ 12 [1080p]</title><infoHash>0123456789012345678901234567890123456789</infoHash></item></channel></rss>";
        var release=Assert.Single(TorrentCatalogFormats.Parse("nyaa",Encoding.UTF8.GetBytes(xml)));
        Assert.True(TorrentCatalogFormats.CoversEpisode(release,12));
        Assert.False(TorrentCatalogFormats.CoversEpisode(release,13));
    }
    [Fact] public void EpisodeCatalogCombinesVariantsAndBatchesWithoutInventingUnknownEpisodes() {
        var results=new[] {
            new TorrentSearchResult("A","magnet:?",Episodes:"03"),
            new TorrentSearchResult("B","magnet:?",Episodes:"01-03"),
            new TorrentSearchResult("C","magnet:?",Episodes:"03"),
            new TorrentSearchResult("Unknown","magnet:?",Episodes:"unknown")
        };
        var episodes=TorrentProvider.CatalogEpisodes("catalog-test",results);
        Assert.Equal(new[]{1,2,3},episodes.Select(e=>e.Number));
        Assert.All(episodes,e=>Assert.Equal("catalog-test",e.ReleaseId));
        Assert.Equal("episode:3",episodes[2].Id);
        Assert.Equal("Серия 3",episodes[2].Title);
    }
    [Fact] public async Task CatalogSurvivesRestartWithoutOpeningTorrentEngine() {
        var folder=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString()); Directory.CreateDirectory(folder);
        var db=new AnimeApp.Services.Database.DatabaseService(Path.Combine(folder,"test.db")); await db.Initialize();
        var repo=new AnimeApp.Services.Database.LibraryRepository(db);
        await using var session=new TorrentSession(); var provider=new TorrentProvider(repo,session);
        var release=await provider.SaveCatalog("shikimori:42","Show",new[]{new TorrentSearchResult("Show","magnet:?",Episodes:"01-02")},default);
        var other=new TorrentProvider(repo,session);
        Assert.Equal(2,(await other.GetEpisodes(release.Id)).Count);
        Assert.Equal("Торрент остановлен",session.Status);
        var updated=await other.SaveCatalog("shikimori:42","Show",new[]{new TorrentSearchResult("Show","magnet:?",Episodes:"01-03")},default);
        Assert.Equal(release.Id,updated.Id);
        Assert.Equal(3,(await other.GetSavedEpisodes(updated.Id)).Count);
    }
    private sealed class TorrentFileHandler(byte[] bytes) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {Content=new ByteArrayContent(bytes)});
    }
    [Fact] public async Task DirectTorrentMetadataIsValidatedBeforeSaving() {
        var folder=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString()); Directory.CreateDirectory(folder);
        var video=Path.Combine(folder,"Show - 01.mkv"); await File.WriteAllBytesAsync(video,new byte[1024]);
        var file=Path.Combine(folder,"show.torrent"); await new MonoTorrent.TorrentCreator().CreateAsync(new MonoTorrent.TorrentFileSource(video),file);
        var bytes=await File.ReadAllBytesAsync(file);
        var db=new AnimeApp.Services.Database.DatabaseService(Path.Combine(folder,"test.db")); await db.Initialize(); var repo=new AnimeApp.Services.Database.LibraryRepository(db);
        await using var session=new TorrentSession(); var provider=new TorrentProvider(repo,session,new TorrentFileHandler(bytes));
        var result=new TorrentSearchResult("Show","magnet:?",TorrentUrl:"https://example.org/show.torrent",InfoHash:new string('0',40));
        await Assert.ThrowsAsync<IOException>(()=>provider.ImportResult(result,default));
        var release=await provider.ImportResult(result with {InfoHash=""},default);
        Assert.Equal("Show",(await provider.GetRelease(release.Id)).Title);
        var catalog=await provider.SaveCatalog("metadata-only","Show",[result with {InfoHash=""}],default);
        Assert.Equal(1,Assert.Single(await provider.GetEpisodes(catalog.Id)).Number);
        Assert.Equal("Торрент остановлен",session.Status);
        Assert.Equal("Торрент остановлен",session.Status);
    }
    [Fact] public void AnimeToshoRetainsTrackerMagnetAndTorrentUrl() {
        var result=Assert.Single(TorrentCatalogFormats.Parse("animetosho",Encoding.UTF8.GetBytes("""[{"title":"Show - 01","info_hash":"0123456789012345678901234567890123456789","magnet_uri":"magnet:?xt=urn:btih:0123456789012345678901234567890123456789&tr=udp%3A%2F%2Ftracker.example%3A80","torrent_url":"https://nyaa.si/download/1.torrent"}]""")));
        Assert.Contains("&tr=",result.Magnet); Assert.EndsWith("1.torrent",result.TorrentUrl);
    }
    [Fact] public void ErrorObjectIsReportedAsSourceFailure() => Assert.Throws<IOException>(()=>TorrentCatalogFormats.Parse("animetosho",Encoding.UTF8.GetBytes("{\"error\":\"unavailable\"}")));
    [Theory]
    [InlineData("01-12",3,true)]
    [InlineData("01",2,false)]
    [InlineData("unknown",1,false)]
    public void AutomaticSelectionRequiresExplicitEpisodeCoverage(string episodes,int episode,bool expected) => Assert.Equal(expected,TorrentCatalogFormats.CoversEpisode(new("Title","magnet:?",Episodes:episodes),episode));
    [Fact] public void NyaaParsesOriginalAudioMetadataWithoutClaimingRussianSubtitles() {
        var result=Assert.Single(TorrentCatalogFormats.Parse("nyaa",Encoding.UTF8.GetBytes("""
        <rss xmlns:n="https://nyaa.si/xmlns/nyaa"><channel><item><title>Show - 02 [1080p]</title><n:infoHash>0123456789012345678901234567890123456789</n:infoHash><n:seeders>12</n:seeders><n:size>1.5 GiB</n:size></item></channel></rss>
        """)));
        Assert.Equal(1610612736,result.Size); Assert.Equal(12,result.Seeds); Assert.Equal("02",result.Episodes);
        Assert.True(result.JapaneseAudioCandidate); Assert.False(TorrentProvider.HasRussianSubtitles(result));
    }
    [Theory]
    [InlineData("Show English Dub")]
    [InlineData("Show Hardsub Dual Audio")]
    public void UnreplaceableSubtitlesAndDubOnlyAreExcluded(string title) {
        var json=System.Text.Json.JsonSerializer.Serialize(new[]{new{title,info_hash="0123456789012345678901234567890123456789",total_size=12,seeders=3}});
        Assert.Empty(TorrentCatalogFormats.Parse("animetosho",Encoding.UTF8.GetBytes(json)));
    }
    [Fact] public void RssExternalEntitiesAreRejected() => Assert.Throws<System.Xml.XmlException>(()=>TorrentCatalogFormats.Parse("nyaa",Encoding.UTF8.GetBytes("<!DOCTYPE rss [<!ENTITY e SYSTEM 'file:///C:/private'>]><rss>&e;</rss>")));
    [Theory]
    [InlineData("[Group] Show Season 2 - 01", "Show Season 2", true)]
    [InlineData("[Group] Show Season 3 - 01", "Show Season 2", false)]
    [InlineData("[Group] Show S02E01", "Show", false)]
    [InlineData("[Group] Show S02E01", "Show 2nd Season", true)]
    public void MatchingKeepsSeasonNumbers(string release,string title,bool expected) => Assert.Equal(expected,TorrentCatalogFormats.MatchesTitle(release,[title]));
    [Fact] public async Task DefaultsAreAddedOnceWithoutReplacingCustomSources() {
        var db=new AnimeApp.Services.Database.DatabaseService(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db")); await db.Initialize();
        var repo=new AnimeApp.Services.Database.LibraryRepository(db);
        await repo.SaveSetting("torrent-search-sources",new[]{new TorrentSearchSource("Custom","https://example.org/?q={query}",false)});
        await using var session=new TorrentSession(); var provider=new TorrentProvider(repo,session);
        await provider.EnsureDefaultCatalogs();
        var sources=await repo.Setting<List<TorrentSearchSource>>("torrent-search-sources");
        Assert.Equal(3,sources!.Count); Assert.False(sources[0].Enabled);
        await repo.SaveSetting("torrent-search-sources",Array.Empty<TorrentSearchSource>());
        await provider.EnsureDefaultCatalogs();
        Assert.Empty((await repo.Setting<List<TorrentSearchSource>>("torrent-search-sources"))!);
    }
}
