using Avalonia;
using System.Net;
using System.Text;
using AnimeApp.Services.Playback;
using Xunit;
namespace AnimeApp.Tests;
public class DownloadTests
{
    [Fact] public async Task OfflineSubtitleIsCopiedAndSurvivesRemovalOfOriginalCache() {
        var root=Root();var subtitle=Path.Combine(root,"translation.ass");await File.WriteAllTextAsync(subtitle,"[Script Info]\nTitle: offline");
        using var http=new HttpClient(new Handler((r,c)=>Task.FromResult(Response(Video))));
        var saved=await new EpisodeDownloader(http).Download(new(PlaybackKind.DirectMedia,"https://media.test/e.mp4",SubtitleTracks:[new(subtitle)]),root,"Show","Auto",null,default);
        File.Delete(subtitle);
        var local=DownloadedEpisodeProvider.Source(saved.Directory);
        Assert.Equal(PlaybackKind.LocalFile,local.Kind);
        var track=Assert.Single(local.SubtitleTracks!);
        Assert.True(File.Exists(track.Location));Assert.StartsWith(saved.Directory,track.Location);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
    private static HttpResponseMessage Response(byte[] data, string type = "video/mp4") { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }; r.Content.Headers.ContentType = new(type); return r; }
    private static byte[] Video => new byte[] {0,0,0,24,(byte)'f',(byte)'t',(byte)'y',(byte)'p',0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0};
    private static string Root() { var path = Path.Combine(Path.GetTempPath(), "AnimeWatchka-download-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    [Fact]
    public async Task DirectDownloadIsAtomicAndNeverOverwritesExistingFiles()
    {
        var root = Root(); var original = Path.Combine(root, "existing.mp4"); await File.WriteAllTextAsync(original, "keep");
        using var http = new HttpClient(new Handler((r, ct) => Task.FromResult(Response(Video))));
        var service = new EpisodeDownloader(http);
        var first = await service.Download(new(PlaybackKind.DirectMedia, "https://media.test/e.mp4"), root, "../ CON:episode", "720p", null, default);
        var second = await service.Download(new(PlaybackKind.DirectMedia, "https://media.test/e.mp4"), root, "../ CON:episode", "720p", null, default);
        Assert.NotEqual(first.Directory, second.Directory); Assert.Equal(Video, await File.ReadAllBytesAsync(first.MediaFile));
        Assert.Equal("keep", await File.ReadAllTextAsync(original)); Assert.Empty(Directory.GetDirectories(root, EpisodeDownloader.PendingName + "*"));
        Assert.Equal(first.MediaFile, DownloadedEpisodeProvider.Read(first.Directory).File);
    }
    [Fact]
    public async Task HlsUsesSelectedQualityAndContainsOnlyLocalResources()
    {
        var root = Root(); var requests = new List<string>();
        using var http = new HttpClient(new Handler((r, ct) => {
            requests.Add(r.RequestUri!.AbsolutePath); Assert.Equal("https://source.test/", r.Headers.Referrer!.ToString());
            var path = r.RequestUri.AbsolutePath;
            var text = path switch {
                "/master.m3u8" => "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100,RESOLUTION=640x480\n480/list.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=200,RESOLUTION=1280x720\n720/list.m3u8",
                "/720/list.m3u8" => "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:4\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:4,\na.m4s?token=secret\n#EXTINF:4,\nb.m4s\n#EXT-X-ENDLIST", _ => "fragment" };
            return Task.FromResult(Response(Encoding.UTF8.GetBytes(text), path.EndsWith("m3u8") ? "application/vnd.apple.mpegurl" : "application/octet-stream"));
        }));
        var result = await new EpisodeDownloader(http).Download(new(PlaybackKind.DirectMedia, "https://media.test/master.m3u8", new Dictionary<string,string>{{"Referer","https://source.test/"}}), root, "Episode", "720p", null, default);
        Assert.DoesNotContain("/480/list.m3u8", requests); Assert.Contains("/720/a.m4s", requests);
        var playlist = await File.ReadAllTextAsync(result.MediaFile); Assert.DoesNotContain("https", playlist); Assert.DoesNotContain("secret", playlist); Assert.Contains("segment-00001.m4s", playlist);
        Assert.Equal(result.MediaFile, DownloadedEpisodeProvider.Read(result.Directory).File);
        Assert.Equal(3, Directory.GetFiles(result.Directory).Count(f => f.EndsWith(".m4s") || f.EndsWith(".mp4")));
    }
    [Theory]
    [InlineData("#EXTM3U\n#EXTINF:2,\na.ts")]
    [InlineData("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key\"\n#EXTINF:2,\na.ts\n#EXT-X-ENDLIST")]
    [InlineData("#EXTM3U\n#EXTINF:2,\nfile:///private.ts\n#EXT-X-ENDLIST")]
    [InlineData("#EXTM3U\n#EXT-X-BYTERANGE:5@0\n#EXTINF:2,\na.ts\n#EXT-X-ENDLIST")]
    public async Task UnsupportedHlsLeavesNoCompletedOrPartialDownload(string playlist)
    {
        var root = Root();
        using var http = new HttpClient(new Handler((r, ct) => Task.FromResult(Response(Encoding.UTF8.GetBytes(playlist), "application/vnd.apple.mpegurl"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => new EpisodeDownloader(http).Download(new(PlaybackKind.DirectMedia, "https://media.test/a.m3u8"), root, "episode", "Auto", null, default));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }
    [Fact]
    public async Task CancelAndHttpFailureRemoveTemporaryFiles()
    {
        var root = Root(); using var cancellation = new CancellationTokenSource();
        var began = new TaskCompletionSource();
        using var http = new HttpClient(new Handler(async (r, ct) => { began.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Response(Video); }));
        var task = new EpisodeDownloader(http).Download(new(PlaybackKind.DirectMedia, "https://media.test/e.mp4"), root, "episode", "Auto", null, cancellation.Token);
        await began.Task; Assert.Single(Directory.GetDirectories(root)); Assert.Equal(0, EpisodeDownloader.RemoveInterrupted(root)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); Assert.Empty(Directory.GetFileSystemEntries(root));
        using var failing = new HttpClient(new Handler((r, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        await Assert.ThrowsAsync<HttpRequestException>(() => new EpisodeDownloader(failing).Download(new(PlaybackKind.DirectMedia, "https://media.test/e.mp4"), root, "episode", "Auto", null, default));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }
    private sealed class PartialStream(TaskCompletionSource started) : MemoryStream(Video)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, ct);
            started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return 0;
        }
    }
    [Fact]
    public async Task CancelAfterWritingBytesRemovesPartialFile()
    {
        var root=Root(); var started=new TaskCompletionSource(); using var cancel=new CancellationTokenSource();
        using var http=new HttpClient(new Handler((r,ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new PartialStream(started))})));
        var task=new EpisodeDownloader(http).Download(new(PlaybackKind.DirectMedia,"https://media.test/e.mp4"),root,"episode","Auto",null,cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Single(Directory.GetFiles(root,"episode.mp4",SearchOption.AllDirectories));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task); Assert.Empty(Directory.GetFileSystemEntries(root));
    }
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task DownloadStatusAndCancelRemainVisibleOutsideCard()
    {
        var (vm, _) = await Stage4UiTests.Create(); vm.Settings.WizardVisible=false;
        vm.Sources.Downloading=true; vm.Sources.DownloadStatus="Тестовая серия · 5 МБ из 20 МБ"; vm.Sources.DownloadIndeterminate=false; vm.Sources.DownloadPercent=25;
        AnimeApp.ViewModels.AppearanceViewModel.Current.Persist=false;
        var window=new AnimeApp.UI.MainWindow {DataContext=vm,Width=1000,Height=600}; window.Show();
        try {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var button=Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Button>().Single(b=>Equals(b.Content,"Отменить скачивание"));
            Assert.True(button.IsEffectivelyVisible); var point=button.TranslatePoint(new Avalonia.Point(0,0),window); Assert.NotNull(point); Assert.InRange(point.Value.Y,0,550);
            using var screenshot=new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1000,600)); screenshot.Render(window);
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"screenshots")); screenshot.Save(Path.Combine(AppContext.BaseDirectory,"screenshots","download.png"));
        } finally { window.Close(); AnimeApp.ViewModels.AppearanceViewModel.Current.Persist=true; }
    }
    [Fact]
    public void CleanupRemovesOnlyRecognizedInterruptedFolders()
    {
        var root=Root(); var partial=Path.Combine(root,EpisodeDownloader.PendingName+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(partial);
        File.WriteAllText(Path.Combine(partial,"download.pending"),"AnimeWatchka download v1"); File.WriteAllText(Path.Combine(partial,"segment.ts"),"partial");
        var unknown=Path.Combine(root,EpisodeDownloader.PendingName+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(unknown); File.WriteAllText(Path.Combine(unknown,"keep.txt"),"user file");
        Assert.Equal(1,EpisodeDownloader.RemoveInterrupted(root)); Assert.False(Directory.Exists(partial)); Assert.True(File.Exists(Path.Combine(unknown,"keep.txt")));
    }
    [Fact]
    public void DownloadQualityPrefersNoHigherThanRequested()
    {
        MediaVariant[] variants=[new("480p","low"),new("1080p","high")];
        Assert.Equal("480p",EpisodeDownloader.SelectQuality(variants,"720p")!.Quality);
        Assert.Equal("1080p",EpisodeDownloader.SelectQuality(variants,"Auto")!.Quality);
    }
    [Fact]
    public async Task DisguisedHtmlAndTruncatedFilesAreNotPublished()
    {
        foreach (var truncate in new[]{false,true}) {
            var root = Root(); using var http = new HttpClient(new Handler((r, ct) => { var response=Response(truncate ? Video : Encoding.UTF8.GetBytes("<html>not a video</html>")); if(truncate) response.Content.Headers.ContentLength=1000; return Task.FromResult(response); }));
            var error = await Record.ExceptionAsync(() => new EpisodeDownloader(http).Download(new(PlaybackKind.DirectMedia,"https://media.test/a.mp4"),root,"episode","Auto",null,default)); Assert.True(error is IOException or InvalidDataException);
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
    }
}
