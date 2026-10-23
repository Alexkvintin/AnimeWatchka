using System.Net;
using System.Text;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Providers;
using Xunit;
namespace AnimeApp.Tests;
public sealed class SeasonDownloadTests
{
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request,ct); }
    private static byte[] Video => [0,0,0,24,(byte)'f',(byte)'t',(byte)'y',(byte)'p',0,0,0,0,0,0,0,0];
    private static HttpResponseMessage Response(byte[] bytes, string type = "video/mp4")
    { var r=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)}; r.Content.Headers.ContentType=new(type);return r; }
    private static string Root() => Path.Combine(Path.GetTempPath(),"AnimeWatchka-season-tests",Guid.NewGuid().ToString("N"));
    private sealed class Provider(string id="voice-a") : IAnimeProvider
    {
        public string Id=>id;public string Language=>"ru";public string DisplayName=>id;
        public int Requests; public int FailEpisode;
        public Task<ProviderAvailability> CheckAvailability(CancellationToken ct=default)=>Task.FromResult(new ProviderAvailability(true,""));
        public Task<IReadOnlyList<ProviderRelease>> Search(string q,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<ProviderRelease>>([]);
        public Task<ProviderRelease> GetRelease(string id,CancellationToken ct=default)=>Task.FromResult(new ProviderRelease(id,"Title"));
        public Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<ProviderEpisode>>([new("1",1,"One"),new("2",2,"Two"),new("3",3,"Three")]);
        public Task<PlaybackSource> GetPlayback(string id,ProviderEpisode e,CancellationToken ct=default)
        { Requests++;if(e.Number==FailEpisode)throw new IOException();return Task.FromResult(new PlaybackSource(PlaybackKind.DirectMedia,"https://media.test/"+e.Id+".mp4")); }
    }
    [Fact]
    public async Task SeasonSkipsReadyEpisodesAndRetriesOnlyFailedOnes()
    {
        var root=Root();var provider=new Provider{FailEpisode=2};using var http=new HttpClient(new Handler((r,c)=>Task.FromResult(Response(Video))));
        var service=new SeasonDownloader(http);var episodes=await provider.GetEpisodes("season");
        var first=await service.Download(provider,new("season","Title"),episodes,root,"Title",42,"720p",null,default);
        Assert.Equal(2,first.Downloaded);Assert.Equal([2],first.Failed);Assert.Equal(2,DownloadLibrary.Read(root).Count);
        provider.FailEpisode=0;
        var second=await service.Download(provider,new("season","Title"),episodes,root,"Title",42,"720p",null,default);
        Assert.Equal(1,second.Downloaded);Assert.Equal(2,second.Skipped);Assert.Equal(4,provider.Requests);
        var saved=DownloadLibrary.Read(root);Assert.Null(DownloadLibrary.Find(saved,"voice-b","season","1"));Assert.Null(DownloadLibrary.Find(saved,"voice-a","season-2","1"));
        Assert.NotNull(DownloadLibrary.Find(saved,"voice-a","season","1"));
        var offline=new DownloadedEpisodeProvider();Assert.Equal(3,(await offline.GetEpisodes(saved[0].Directory)).Count);
        File.Delete(saved[0].File);Assert.Equal(2,DownloadLibrary.Read(root).Count);
    }
    [Fact]
    public async Task LocalPlaybackDoesNotRequestOnlineStreamAndMissingFileFallsBack()
    {
        var root=Root();var p=new Provider();using var http=new HttpClient(new Handler((r,c)=>Task.FromResult(Response(Video))));
        await new SeasonDownloader(http).Download(p,new("season","Title"),[new("1",1,"One")],root,"Title",42,"720p",null,default);
        var db=new AnimeApp.Services.Database.DatabaseService(Path.Combine(Root(),"db"));await db.Initialize();var repo=new AnimeApp.Services.Database.LibraryRepository(db);
        await repo.SaveSetting("downloads",new DownloadPreferences(false,root));
        var source=await DownloadLibrary.Resolve(repo,p,"season",new("1",1,"One"));Assert.Equal(PlaybackKind.LocalFile,source.Kind);Assert.Equal(1,p.Requests);
        File.Delete(source.Location);
        Assert.Equal(PlaybackKind.DirectMedia,(await DownloadLibrary.Resolve(repo,p,"season",new("1",1,"One"))).Kind);Assert.Equal(2,p.Requests);
    }
    [Fact]
    public async Task HlsFetchesAtMostFourSegmentsConcurrentlyAndRetriesTransientFailure()
    {
        var root=Root();int active=0,maximum=0,attempts=0;
        var barrier=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http=new HttpClient(new Handler(async (r,ct)=>{
            if(r.RequestUri!.AbsolutePath.EndsWith("m3u8")) return Response(Encoding.UTF8.GetBytes("#EXTM3U\n"+string.Concat(Enumerable.Range(0,8).Select(i=>$"#EXTINF:2,\n{i}.ts\n"))+"#EXT-X-ENDLIST"),"application/vnd.apple.mpegurl");
            if(r.RequestUri.AbsolutePath=="/0.ts" && Interlocked.Increment(ref attempts)==1) return new(HttpStatusCode.ServiceUnavailable);
            var current=Interlocked.Increment(ref active);Interlocked.Exchange(ref maximum,Math.Max(current,maximum));if(current>=3)barrier.TrySetResult();
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5),ct);await Task.Delay(30,ct);Interlocked.Decrement(ref active);return Response([1,2,3],"video/mp2t");
        }));
        var result=await new EpisodeDownloader(http).Download(new(PlaybackKind.DirectMedia,"https://media.test/list.m3u8"),root,"Title","Auto",null,default);
        Assert.InRange(maximum,3,4);Assert.Equal(2,attempts);Assert.Equal(8,Directory.GetFiles(result.Directory,"segment-*").Length);
        Assert.Equal(result.MediaFile,DownloadedEpisodeProvider.Read(result.Directory).File);
    }
    [Fact]
    public void DefaultRootIsInsideApplicationAndCustomRootMustBeAbsolute()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory,"Downloads"),new DownloadPreferences().Root);
        Assert.Throws<IOException>(()=>new DownloadPreferences(false,"relative").Root);
        var root=Root();Assert.Equal(Path.GetFullPath(root),new DownloadPreferences(false,root).Root);
    }
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task DownloadLibraryWindowShowsPlayableRowsAtSmallWindowSize()
    {
        var (vm,repo)=await Stage4UiTests.Create();vm.Settings.WizardVisible=false;
        var root=Root();await repo.SaveSetting("downloads",new DownloadPreferences(false,root));
        var p=new Provider();using var http=new HttpClient(new Handler((r,c)=>Task.FromResult(Response(Video))));
        await new SeasonDownloader(http).Download(p,new("season","Title"),[new("1",1,"One")],root,"Тестовый сезон",42,"720p",null,default);
        await vm.Sources.ShowDownloadsCommand.ExecuteAsync(null);
        AnimeApp.ViewModels.AppearanceViewModel.Current.Persist=false;
        var window=new AnimeApp.UI.MainWindow{DataContext=vm,Width=1000,Height=600};window.Show();
        try {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            var play=Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Button>().Single(b=>b.CommandParameter is DownloadEntry);
            Assert.True(play.IsEffectivelyVisible);
            using var screenshot=new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1000,600));screenshot.Render(window);
            var directory=Path.Combine(AppContext.BaseDirectory,"screenshots");Directory.CreateDirectory(directory);screenshot.Save(Path.Combine(directory,"download-library.png"));
        } finally {window.Close();AnimeApp.ViewModels.AppearanceViewModel.Current.Persist=true;}
    }
}
