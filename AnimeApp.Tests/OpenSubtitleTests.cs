using System.Text.Json;
using AnimeApp.Services.Playback;
using Xunit;
namespace AnimeApp.Tests;
public class OpenSubtitleTests {
    private sealed class ApiHandler : HttpMessageHandler {
        public int Calls;
        public string DownloadLink="https://dl.opensubtitles.com/download/test.srt";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            Calls++;
            Assert.Equal("test-key",request.Headers.GetValues("Api-Key").Single());
            Assert.Equal("api.opensubtitles.com",request.RequestUri!.Host);
            var body=request.RequestUri.AbsolutePath.EndsWith("/download")
                ? JsonSerializer.Serialize(new {link=DownloadLink})
                : """{"data":[{"attributes":{"language":"ru","feature_details":{"year":1998,"season_number":1,"episode_number":2,"parent_title":"Cowboy Bebop"},"files":[{"file_id":123}],"uploader":{"name":"Translator"}}}]}""";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {Content=new StringContent(body)});
        }
    }
    [Fact] public async Task OwnKeySearchAndDownloadUseOfficialApiAndRejectForeignDownloadHost() {
        var store=new AnimeApp.Services.Shikimori.ProtectedFile<OpenSubtitleCredentials>(Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString(),"credentials.dat"));
        var handler=new ApiHandler(); var service=new OpenSubtitlesService(new(handler),store);
        Assert.Empty(await service.Find(["Cowboy Bebop"],1998,2,"tv",default)); Assert.Equal(0,handler.Calls);
        await service.Login("test-key","","");
        var result=Assert.Single(await service.Find(["Cowboy Bebop"],1998,2,"tv",default));
        Assert.Equal("OpenSubtitles",result.Service); Assert.Equal("srt",result.Format);
        Assert.StartsWith("https://dl.opensubtitles.com/",await service.DownloadUrl(result.Id,default));
        handler.DownloadLink="https://unrelated.example/private";
        await Assert.ThrowsAsync<IOException>(()=>service.DownloadUrl(result.Id,default));
        await service.Clear(); Assert.False(await service.Configured());
    }
    [Theory]
    [InlineData(1998,1,2,"ru",true)]
    [InlineData(2021,1,2,"ru",false)]
    [InlineData(1998,2,2,"ru",false)]
    [InlineData(1998,1,3,"ru",false)]
    [InlineData(1998,1,2,"en",true)]
    [InlineData(1998,1,2,"uk",true)]
    [InlineData(1998,1,2,"fr",false)]
    public void ExactTitleYearSeasonEpisodeAndLanguageRequired(int year,int season,int episode,string language,bool expected) {
        using var doc=JsonDocument.Parse(JsonSerializer.Serialize(new{language,feature_details=new{year,season_number=season,episode_number=episode,title="Episode title",parent_title="Cowboy Bebop"}}));
        Assert.Equal(expected,OpenSubtitlesService.Matches(doc.RootElement,["Cowboy Bebop"],1998,2,"tv"));
    }
}
