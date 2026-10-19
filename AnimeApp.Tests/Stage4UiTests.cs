using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AnimeApp.Models;
using AnimeApp.Services;
using AnimeApp.Services.Database;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Shikimori;
using AnimeApp.UI;
using AnimeApp.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AnimeApp.Tests;
public sealed class Stage4UiTests
{
    private sealed class Tokens : ITokenStore
    {
        public Task<OAuthToken?> Read(CancellationToken ct = default) => Task.FromResult<OAuthToken?>(null);
        public Task Write(OAuthToken t, CancellationToken ct = default) => Task.CompletedTask;
        public Task Clear(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Http : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath;
            var id = path.Split('/').Last();
            var json = path.EndsWith("related") ? "[]" : $"{{\"id\":{id},\"name\":\"Test\",\"kind\":\"tv\"}}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class Provider(string id, string language = "ru") : IAnimeProvider
    {
        public string Id => id;
        public string Language => language;
        public string DisplayName => id;

        public int Searches;
        public Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default) => Task.FromResult(new ProviderAvailability(true, ""));
        public Task<IReadOnlyList<ProviderRelease>> Search(string q, CancellationToken ct = default)
        {
            Searches++;
            return Task.FromResult<IReadOnlyList<ProviderRelease>>([]);
        }

        public Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default) => Task.FromResult(new ProviderRelease(id, "Saved"));
        public Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderEpisode>>([new("1", 1, "Episode"), new("2", 2, "Episode")]);
        public Task<PlaybackSource> GetPlayback(string id, ProviderEpisode e, CancellationToken ct = default) => Task.FromResult(new PlaybackSource(PlaybackKind.WebPage, "https://example.org"));
    }

    internal static async Task<(MainViewModel Vm, LibraryRepository Repo)> Create(params IAnimeProvider[] providers)
    {
        var db = new DatabaseService(Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString(), "db"));
        await db.Initialize();
        var repo = new LibraryRepository(db);
        var tokens = new Tokens();
        var api = new ShikimoriService(new(new Http()), new(), tokens);
        var vm = new MainViewModel(repo, api, new SyncService(repo, api), tokens, new PlaybackService(repo, new EmbeddedPlaybackLauncher()), new ProviderRegistry(providers), NullLogger<MainViewModel>.Instance);
        await vm.Initialize();
        vm.UserId = 7; vm.Settings.AutoSync = false; await vm.Reload();
        return (vm, repo);
    }

    [AvaloniaFact]
    public async Task ThousandTitlesCreateOnlyViewportCardsAndRowsPreserveOrder()
    {
        var(vm, repo) = await Create();
        await repo.Cache(7, Enumerable.Range(1, 1200).Select(i => new Anime { ShikimoriId = i, Name = $"Anime {i}", UserStatus = "planned" }));
        await vm.Initialize();
        vm.UserId = 7; vm.Settings.AutoSync = false; await vm.Reload();
        vm.NavigateCommand.Execute("Запланировано");
        var w = new MainWindow
        {
            DataContext = vm
        };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.Equal(1200, vm.Items.Count);
        Assert.Equal(vm.Items.Select(a => a.ShikimoriId), vm.Rows.SelectMany(r => r).Select(a => a.ShikimoriId));
        var cards = w.GetVisualDescendants().OfType<Button>().Count(b => b.Command == vm.OpenAnimeCommand);
        Assert.InRange(cards, 1, 50);
        vm.SetColumns(2);
        Assert.All(vm.Rows, r => Assert.InRange(r.Length, 1, 2));
        w.Close();
    }

    [AvaloniaFact]
    public async Task SavedMappingWinsOverPriorityAndLanguageWithoutSearching()
    {
        var mapped = new Provider("mapped", "ru");
        var preferred = new Provider("preferred", "uk");
        var(vm, repo) = await Create(preferred, mapped);
        await repo.SaveMapping(new(1, "mapped", "release"));
        await vm.OpenAnimeCommand.ExecuteAsync(new Anime { ShikimoriId = 1, Name = "Test" });
        Assert.Equal("mapped", vm.Sources.SelectedProvider!.Id);
        Assert.False(vm.Sources.IsChoosing);
        Assert.Equal(2, vm.Sources.Episodes.Count);
        Assert.Equal(0, mapped.Searches + preferred.Searches);
    }

    private sealed class DelayedProvider : IAnimeProvider
    {
        public string Id => "delayed";
        public string DisplayName => Id;
        public string Language => "ru";

        public TaskCompletionSource<IReadOnlyList<ProviderEpisode>> Result = new();
        public TaskCompletionSource<bool> Started = new();
        public Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default) => Task.FromResult(new ProviderAvailability(true, ""));
        public Task<IReadOnlyList<ProviderRelease>> Search(string q, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderRelease>>([]);
        public Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default) => Task.FromResult(new ProviderRelease(id, id));
        public Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            return Result.Task;
        }

        public Task<PlaybackSource> GetPlayback(string id, ProviderEpisode episode, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [AvaloniaFact]
    public async Task LateEpisodesCannotReplaceAnotherSelectedRelease()
    {
        var provider = new DelayedProvider();
        var(vm, _) = await Create(provider);
        vm.Sources.SelectedRelease = new("first", "First");
        var loading = vm.Sources.LoadEpisodesCore();
        await provider.Started.Task;
        vm.Sources.SelectedRelease = new("second", "Second");
        provider.Result.SetResult([new("old", 1, "Old episode")]);
        await loading;
        Assert.Empty(vm.Sources.Episodes);
        Assert.Equal("second", vm.Sources.SelectedRelease.Id);
    }

    [AvaloniaFact]
    public async Task SourceOrderTakesPrecedenceOverLanguageAndOldPreference()
    {
        var(vm, _) = await Create(new Provider("ru", "ru"), new Provider("uk", "uk"), new Provider("uk2", "uk"));
        vm.Sources.Providers[1].Priority = 5;
        vm.Sources.Providers[2].Priority = 1;
        vm.Sources.RefreshProviders();
        Assert.Equal("ru", vm.Sources.SelectedProvider!.Id);
        vm.Sources.PreferredProviderId = "ru";
        vm.Sources.RefreshProviders();
        Assert.Equal("ru", vm.Sources.SelectedProvider!.Id);
        vm.Sources.Providers[0].Enabled = false;
        vm.Sources.RefreshProviders();
        Assert.Equal("uk2", vm.Sources.SelectedProvider!.Id);
    }

    [AvaloniaFact]
    public async Task RelatedNavigationOpensNormalCardOfDifferentId()
    {
        var(vm, _) = await Create();
        await vm.OpenAnimeCommand.ExecuteAsync(new Anime { ShikimoriId = 1 });
        await vm.OpenRelatedCommand.ExecuteAsync(new Anime { ShikimoriId = 2 });
        Assert.Equal(2, vm.SelectedAnime!.ShikimoriId);
        Assert.True(vm.IsDetails);
    }

    [AvaloniaFact]
    public void FullscreenRestoresWindowGeometryAndHeader()
    {
        var w = new PlayerWindow
        {
            Width = 1000,
            Height = 700,
            Position = new PixelPoint(20, 30)
        };
        w.Show();
        var state = w.WindowState;
        var size = new Size(w.Width, w.Height);
        var pos = w.Position;
        w.ToggleFullscreen();
        Assert.True(w.Fullscreen.Active);
        Assert.False(w.FindControl<Grid>("Header")!.IsVisible);
        Assert.Equal(new Thickness(0), w.FindControl<Grid>("PlayerRoot")!.Margin);
        w.ToggleFullscreen();
        Assert.Equal(state, w.WindowState);
        Assert.Equal(size, new Size(w.Width, w.Height));
        Assert.Equal(pos, w.Position);
        Assert.True(w.FindControl<Grid>("Header")!.IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public async Task SpacePausesWithoutActivatingFocusedExpander()
    {
        var(vm, engine, _) = await PlayerViewModelTests.Create();
        var window = new PlayerWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        await vm.Tick();
        vm.ShowOptions = true;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var expander = window.GetVisualDescendants().OfType<Expander>().Single();
        expander.IsExpanded = true;
        window.UpdateLayout();
        var header = expander.GetVisualDescendants().OfType<Button>().First();
        header.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Assert.False(engine.Snapshot.Playing);
        Assert.True(expander.IsExpanded);
        await window.Shutdown();
    }

    private sealed class Calendar(Anime a) : IScheduleProvider
    {
        public Task<IReadOnlyList<ScheduleItem>> Schedule(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScheduleItem>>([new(a, 2, DateTimeOffset.Now), new(a, 3, DateTimeOffset.Now)]);
    }

    [AvaloniaFact]
    public async Task ScheduleOpensEpisodesAndPosterWithoutConfirmationAndBackRestoresPage()
    {
        var(vm, repo) = await Create(new Provider("test"));
        vm.NavigateCommand.Execute("Онгоинги");
        var item = new ScheduleItem(new() { ShikimoriId = 1, Name = "Test" }, 1, null)
        {
            ProviderId = "test",
            Release = new("release", "Test", Poster: "https://example.org/poster.jpg")
        };
        await vm.OpenScheduleCommand.ExecuteAsync(item);
        Assert.True(vm.IsDetails);
        Assert.False(vm.Sources.IsChoosing);
        Assert.Equal(2, vm.Sources.Episodes.Count);
        Assert.Equal("https://example.org/poster.jpg", vm.SelectedAnime!.Poster);
        await vm.GoBackCommand.ExecuteAsync(null);
        Assert.True(vm.IsSchedule);
    }

    private sealed class SlowCalendar : IScheduleProvider
    {
        public TaskCompletionSource<IReadOnlyList<ScheduleItem>> Response = new();
        public Task<IReadOnlyList<ScheduleItem>> Schedule(CancellationToken ct = default) => Response.Task;
    }

    [AvaloniaFact]
    public async Task CachedCalendarAppearsBeforeNetworkCompletes()
    {
        var(vm, repo) = await Create();
        var remote = new SlowCalendar();
        await repo.SaveSetting("schedule-v2:general", new[] { new ScheduleItem(new() { Name = "Cached" }, 1, DateTimeOffset.Now) });
        var schedule = new ScheduleViewModel(remote, repo, vm.Sources)
        {
            Mode = "Общее"
        };
        await schedule.Refresh().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Cached", Assert.Single(schedule.Items).Anime.Name);
        Assert.False(remote.Response.Task.IsCompleted);
        remote.Response.SetResult([]);
    }

    [AvaloniaFact]
    public async Task CalendarOnlyMarksEpisodeActuallyListedByMappedSource()
    {
        var(vm, repo) = await Create(new Provider("test"));
        await repo.SaveMapping(new(1, "test", "release"));
        var schedule = new ScheduleViewModel(new Calendar(new() { ShikimoriId = 1 }), repo, vm.Sources)
        {
            Mode = "Общее"
        };
        await schedule.Refresh();
        Assert.Contains("доступна", schedule.Items[0].Availability);
        Assert.Contains("ожидается", schedule.Items[1].Availability);
    }

    [AvaloniaFact]
    public async Task WizardCompletionSurvivesRestartWithoutEnablingUnsupportedMetadata()
    {
        var(vm, repo) = await Create();
        Assert.True(vm.Settings.WizardVisible);
        vm.Settings.SelectedMetadata = vm.Settings.MetadataChoices.Single(c => !c.Supported);
        await vm.Settings.FinishWizardCommand.ExecuteAsync(null);
        Assert.True(vm.Settings.WizardVisible);
        vm.Settings.SelectedMetadata = vm.Settings.MetadataChoices[0];
        await vm.Settings.FinishWizardCommand.ExecuteAsync(null);
        Assert.False(vm.Settings.WizardVisible);
        await vm.Initialize();
        vm.UserId = 7; vm.Settings.AutoSync = false; await vm.Reload();
        Assert.False(vm.Settings.WizardVisible);
        Assert.True(await repo.Setting<bool>("wizard-complete"));
    }

    private sealed class StudioCalendar : IAnimeProvider, IScheduleProvider
    {
        public string Id => "studio";
        public string DisplayName => "Studio";
        public string Language => "ru";
        public ProviderCapabilities Features => ProviderCapabilities.Schedule | ProviderCapabilities.Episodes;

        public Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default) => Task.FromResult(new ProviderAvailability(true, ""));
        public Task<IReadOnlyList<ProviderRelease>> Search(string q, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderRelease>>([]);
        public Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default) => Task.FromResult(new ProviderRelease(id, "Own"));
        public Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderEpisode>>([]);
        public Task<PlaybackSource> GetPlayback(string id, ProviderEpisode e, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ScheduleItem>> Schedule(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScheduleItem>>([new(new() { Name = "Own" }, 1, null) { ProviderId = Id, ProviderName = DisplayName, Release = new("one", "Own"), ProviderDay = DateTime.Now.DayOfWeek }, new(new() { Name = "Foreign calendar" }, 1, DateTimeOffset.Now), new(new() { Name = "Foreign source" }, 1, null) { ProviderId = "other", Release = new("two", "Foreign"), ProviderDay = DateTime.Now.DayOfWeek }]);
    }

    [AvaloniaFact]
    public async Task SourceCalendarDefaultsToOwnSourceAndRejectsUnconfirmedItems()
    {
        var source = new StudioCalendar();
        var(vm, repo) = await Create(source);
        var schedule = new ScheduleViewModel(new Calendar(new() { ShikimoriId = 99 }), repo, vm.Sources);
        Assert.Equal("Мой источник", schedule.Mode);
        await schedule.Refresh();
        Assert.Equal("Own", Assert.Single(schedule.Items).Anime.Title);
        Assert.Equal("studio", schedule.SelectedSource!.Id);
        schedule.OnlyLibrary = true;
        await schedule.Refresh();
        Assert.Empty(schedule.Items);
    }

    [AvaloniaFact]
    public async Task LocalYearFilterSurvivesReloadAndDoesNotNeedRemoteSearch()
    {
        var(vm, repo) = await Create();
        await repo.Cache(7, [new() { ShikimoriId = 1, Name = "Old", Year = 2020, UserStatus = "planned" }, new() { ShikimoriId = 2, Name = "New", Year = 2024, UserStatus = "planned" }]);
        await vm.Initialize();
        vm.UserId = 7; vm.Settings.AutoSync = false; await vm.Reload();
        vm.NavigateCommand.Execute("Запланировано");
        vm.LibraryYear = "2024";
        Assert.Equal("New", Assert.Single(vm.Items).Name);
        await vm.Initialize();
        vm.UserId = 7; vm.Settings.AutoSync = false; await vm.Reload();
        Assert.Equal("2024", vm.LibraryYear);
        Assert.Single(vm.Items);
    }
}
