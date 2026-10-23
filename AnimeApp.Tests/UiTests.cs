using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using AnimeApp.Services.Database;
using AnimeApp.ViewModels;
using AnimeApp.UI;
using Xunit;
using AnimeApp.Models;
using AnimeApp.Providers;
using AnimeApp.Services;
using AnimeApp.Services.Shikimori;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.VisualTree;

[assembly: AvaloniaTestApplication(typeof(AnimeApp.Tests.TestAppBuilder))]
namespace AnimeApp.Tests;
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class UiTests
{
    [AvaloniaFact]
    public async Task CardsDetailsSettingsMappingAndPlaybackAreConnected()
    {
        var path = Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(path, "Test"));
        await File.WriteAllTextAsync(Path.Combine(path, "Test", "01.mp4"), "");
        var db = new DatabaseService(Path.Combine(path, "db.sqlite"));
        await db.Initialize();
        var repo = new LibraryRepository(db);
        await repo.Cache(7, [new Anime { ShikimoriId = 1, Name = "Test", RussianName = "Тестовая история", EpisodesTotal = 12, EpisodesWatched = 2, UserStatus = "watching" }]);
        var token = new EmptyTokens();
        var http = new HttpClient(new DetailsHandler());
        var api = new ShikimoriService(http, new(), token);
        var launcher = new TestLauncher();
        var vm = new MainViewModel(repo, api, new SyncService(repo, api), token, new PlaybackService(repo, launcher), new ProviderRegistry([new LocalFilesProvider(repo)]), NullLogger<MainViewModel>.Instance);
        await repo.SaveSetting("show-adult-content", true); // These UI fixtures do not provide age metadata.
        await vm.Initialize();
        vm.UserId = 7; vm.Settings.AutoSync = false; await vm.Reload();
        var w = new MainWindow
        {
            DataContext = vm
        };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.Items);
        var card = w.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("titleLink"));
        Assert.Equal(vm.OpenAnimeCommand, card.Command);
        Assert.Equal(vm.Items[0], card.CommandParameter);
        Assert.NotNull(card.Command);
        card.Command!.Execute(card.CommandParameter);
        for (var wait = 0; wait < 100 && vm.SelectedAnime?.Description != "Description"; wait++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        await vm.CardReady.WaitAsync(TimeSpan.FromSeconds(10));
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.GetVisualDescendants().OfType<DetailsView>().Single().IsVisible);
        Assert.Equal("Description", vm.SelectedAnime!.Description);
        await vm.CardReady.WaitAsync(TimeSpan.FromSeconds(10));
        vm.Watched = 3;
        await vm.SaveProgressCommand.ExecuteAsync(null);
        Assert.Equal(3, Assert.Single(await repo.Library(7)).EpisodesWatched);
        Assert.Equal(1, vm.PendingCount);
        vm.Settings.LocalFolder = path;
        vm.Settings.Threshold = 95;
        await vm.Settings.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(95, (await repo.Setting<AppSettings>("playback"))!.Threshold);
        vm.Sources.SourceQuery = "Test";
        await vm.Sources.SearchSourceCommand.ExecuteAsync(null);
        vm.Sources.SelectedRelease = Assert.Single(vm.Sources.Releases);
        await vm.Sources.LinkSourceCommand.ExecuteAsync(null);
        Assert.Single(vm.Sources.Episodes);
        Assert.NotNull(await repo.Mapping(1, "local"));
        await vm.Sources.PlayCommand.ExecuteAsync(null);
        Assert.Equal(1, launcher.Last!.ShikimoriId);
        Assert.Single(await repo.History(7));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
        using (var bmp = new RenderTargetBitmap(new PixelSize(1380, 900)))
        {
            bmp.Render(w);
            bmp.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "details.png"));
        }

        w.Close();
        http.Dispose();
    }

    private sealed class TestLauncher : IPlaybackLauncher
    {
        public PlaybackRequest? Last;
        public Task Open(PlaybackRequest r, CancellationToken ct = default)
        {
            Last = r;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyTokens : ITokenStore
    {
        public Task<OAuthToken?> Read(CancellationToken ct = default) => Task.FromResult<OAuthToken?>(null);
        public Task Write(OAuthToken t, CancellationToken ct = default) => Task.CompletedTask;
        public Task Clear(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DetailsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"id":1,"name":"Test","russian":"Тестовая история","kind":"tv","status":"released","episodes":12,"description_html":"<p>Description</p>","aired_on":"2023-01-01"}""") });
    }

    [AvaloniaFact]
    public async Task WindowRendersAndNavigatesAllPrimaryPages()
    {
        using var services = App.CreateServices(Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString()));
        await services.GetRequiredService<DatabaseService>().Initialize();
        var repo = services.GetRequiredService<LibraryRepository>();
        var vm = services.GetRequiredService<MainViewModel>();
        await repo.SaveSetting("show-adult-content", true); // These UI fixtures do not provide age metadata.
        await vm.Initialize();
        vm.UserId = 7; vm.Settings.AutoSync = false; await vm.Reload();
        var w = new MainWindow
        {
            DataContext = vm
        };
        w.Show();
        foreach (var page in new[]
        {
            "Главная",
            "Смотрю",
            "Запланировано",
            "История",
            "Поиск",
            "Настройки",
            "Завершено",
            "Отложено",
            "Брошено",
            "Пересматриваю"
        }

        )
        {
            vm.NavigateCommand.Execute(page);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(page, vm.Page);
            Assert.True(w.IsVisible);
        }

        vm.NavigateCommand.Execute("Настройки");
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
        using (var bmp = new RenderTargetBitmap(new PixelSize(1380, 900)))
        {
            bmp.Render(w);
            bmp.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "settings.png"));
        }

        vm.NavigateCommand.Execute("Главная");
        Dispatcher.UIThread.RunJobs();
        using (var bmp = new RenderTargetBitmap(new PixelSize(1380, 900)))
        {
            bmp.Render(w);
            bmp.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "home.png"));
        }

        w.Close();
    }
}

