using AnimeApp.Services.Database;
using AnimeApp.ViewModels;
using AnimeApp.UI;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AnimeApp.Tests;
public class TorrentUiTests
{
    private sealed class DelayedProvider : AnimeApp.Services.Providers.IAnimeProvider {
        public string Id => "delayed-test";
        public string DisplayName => "Delayed";
        public string Language => "ja";
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AnimeApp.Services.Providers.ProviderRelease> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AnimeApp.Services.Providers.ProviderAvailability> CheckAvailability(CancellationToken ct=default) => Task.FromResult(new AnimeApp.Services.Providers.ProviderAvailability(true,""));
        public Task<IReadOnlyList<AnimeApp.Services.Providers.ProviderRelease>> Search(string query,CancellationToken ct=default) => Task.FromResult<IReadOnlyList<AnimeApp.Services.Providers.ProviderRelease>>([]);
        public Task<AnimeApp.Services.Providers.ProviderRelease> GetRelease(string id,CancellationToken ct=default) { Started.TrySetResult(); return Response.Task; }
        public Task<IReadOnlyList<AnimeApp.Services.Providers.ProviderEpisode>> GetEpisodes(string id,CancellationToken ct=default) => Task.FromResult<IReadOnlyList<AnimeApp.Services.Providers.ProviderEpisode>>([]);
        public Task<AnimeApp.Services.Playback.PlaybackSource> GetPlayback(string id,AnimeApp.Services.Providers.ProviderEpisode episode,CancellationToken ct=default) => throw new NotSupportedException();
    }
    [AvaloniaFact]
    public async Task ManualSourceChoiceSurvivesDelayedSavedSourceResponse() {
        using var services=App.CreateServices(Path.Combine(Path.GetTempPath(),"AnimeAppTests",Guid.NewGuid().ToString()));
        await services.GetRequiredService<DatabaseService>().Initialize();
        await services.GetRequiredService<LibraryRepository>().SaveSetting("torrent-default-catalogs-v1",true);
        var vm=services.GetRequiredService<MainViewModel>();
        var delayed=new DelayedProvider(); vm.Sources.Register(delayed);
        vm.Sources.EnabledProviders.Add(vm.Sources.Providers.Single(p=>p.Id==delayed.Id));
        await services.GetRequiredService<LibraryRepository>().SaveMapping(new(42,delayed.Id,"saved"));
        vm.SelectedAnime=new() { ShikimoriId=42, Name="Original", RussianName="Русское" };
        var automatic=vm.Sources.SelectForAnime();
        await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var revision=vm.Sources.SelectionRevision;
        vm.Sources.SelectedProvider=vm.Sources.Providers.Single(p=>p.Id=="torrent-subtitles");
        delayed.Response.SetResult(new("saved","Old release"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>automatic);
        Assert.True(vm.Sources.IsTorrent);
        Assert.True(vm.Sources.SelectionRevision>revision);
        Assert.NotEqual("saved",vm.Sources.SelectedRelease?.Id);
    }
    [AvaloniaFact]
    public async Task TorrentSelectionRendersWithoutStartingNetworkEngine()
    {
        using var services=App.CreateServices(Path.Combine(Path.GetTempPath(),"AnimeAppTests",Guid.NewGuid().ToString()));
        await services.GetRequiredService<DatabaseService>().Initialize();
        await services.GetRequiredService<LibraryRepository>().SaveSetting("torrent-default-catalogs-v1",true);
        var vm=services.GetRequiredService<MainViewModel>();
        vm.Sources.SelectedProvider=vm.Sources.Providers.Single(p=>p.Id=="torrent-subtitles");
        vm.Sources.TorrentResults.Add(new("Demo - 01 [1080p]","magnet:?xt=urn:btih:0123456789012345678901234567890123456789","1080p",1024*1024*512,12,"1–12"));
        vm.Sources.SelectedTorrent=vm.Sources.TorrentResults[0];
        var window=new Window { Width=1100,Height=850,DataContext=vm,Content=new DetailsView() };
        window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        Assert.True(vm.Sources.IsTorrent);
        Assert.False(vm.Sources.TorrentBusy);
        using var image=new RenderTargetBitmap(new PixelSize(1100,850)); image.Render(window);
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"screenshots"));
        image.Save(Path.Combine(AppContext.BaseDirectory,"screenshots","torrent-selection.png"));
        window.Close();
    }
}
