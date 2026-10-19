using System.Net;
using Avalonia.Controls;
using AnimeApp.Models;
using AnimeApp.Services;
using AnimeApp.Services.Database;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Shikimori;
using AnimeApp.ViewModels;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AnimeApp.Tests;
public sealed class LibrarySyncRegressionTests
{
    private static Anime Title(long id) => new() { ShikimoriId = id, RateId = id + 100, UserStatus = "watching", EpisodesWatched = 2, EpisodesTotal = 12, Year = 2022 };
    private static async Task<LibraryRepository> Repository()
    {
        var db = new DatabaseService(Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString(), "db"));
        await db.Initialize();
        return new(db);
    }
    private sealed class Api(params Anime[] titles) : IShikimoriService
    {
        public List<Anime> Values = titles.ToList();
        public long Reject;
        public bool IgnoreWrites;
        public Func<Task>? AfterWrite;
        public Task<UserIdentity> CurrentUser(CancellationToken ct = default) => Task.FromResult(new UserIdentity(7, "test"));
        public Task<List<Anime>> Rates(long user, CancellationToken ct = default) => throw new Exception("Must not download entire library to send an edit");
        public Task<Anime?> ReadRate(long user, Anime a, CancellationToken ct = default) => Task.FromResult(Values.FirstOrDefault(x => x.ShikimoriId == a.ShikimoriId));
        public Task Update(long user, Anime a, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task UpdateFields(long user, Anime a, PendingChange p, CancellationToken ct = default)
        {
            if (a.ShikimoriId == Reject) throw new ShikimoriApiException(HttpStatusCode.UnprocessableEntity);
            if (!IgnoreWrites) Values[Values.FindIndex(x => x.ShikimoriId == a.ShikimoriId)] = a with { Score = p.Score ?? a.Score, EpisodesWatched = p.EpisodesChanged ? p.Episodes : a.EpisodesWatched };
            if (AfterWrite is not null) await AfterWrite();
        }
    }
    [Fact]
    public async Task FastSendIsTargetedAndDoesNotDeleteOtherTitles()
    {
        var repo = await Repository(); var a = Title(1); var b = Title(2);
        await repo.Cache(7, [a, b]);
        await repo.Change(7, a, a with { Score = 9 });
        var api = new Api(a, b);
        await new SyncService(repo, api).SendPending(7);
        Assert.Empty(await repo.Pending(7));
        Assert.Equal(2, (await repo.Library(7)).Count);
        Assert.Equal(9, api.Values[0].Score);
        Assert.Equal(2, api.Values[0].EpisodesWatched);
    }
    [Fact]
    public async Task RejectedTitleDoesNotBlockAnotherAndRemainsQueued()
    {
        var repo = await Repository(); var a = Title(1); var b = Title(2);
        await repo.Change(7, a, a with { EpisodesWatched = 3 });
        await repo.Change(7, b, b with { EpisodesWatched = 4 });
        var api = new Api(a, b) { Reject = 1 };
        Assert.Contains("422", await new SyncService(repo, api).SendPending(7));
        Assert.Equal(1, Assert.Single(await repo.Pending(7)).AnimeId);
        Assert.Equal(4, api.Values[1].EpisodesWatched);
    }
    [Fact]
    public async Task FastSendKeepsUnconfirmedAndNewerEdits()
    {
        var repo = await Repository(); var a = Title(1);
        await repo.Change(7, a, a with { Score = 8 });
        var api = new Api(a) { IgnoreWrites = true };
        var sync = new SyncService(repo, api);
        await sync.SendPending(7);
        Assert.Single(await repo.Pending(7));
        api.IgnoreWrites = false;
        api.AfterWrite = () => repo.Change(7, a with { Score = 8 }, a with { Score = 10 });
        await sync.SendPending(7);
        Assert.Equal(10, Assert.Single(await repo.Pending(7)).Score);
        Assert.Equal(10, Assert.Single(await repo.Library(7)).Score);
        api.AfterWrite = null;
        await sync.SendPending(7);
        Assert.Empty(await repo.Pending(7));
        Assert.Equal(10, api.Values[0].Score);
    }
    private sealed class Tokens : ITokenStore
    {
        public Task<OAuthToken?> Read(CancellationToken ct = default) => Task.FromResult<OAuthToken?>(null);
        public Task Write(OAuthToken t, CancellationToken ct = default) => Task.CompletedTask;
        public Task Clear(CancellationToken ct = default) => Task.CompletedTask;
    }
    [Theory]
    [InlineData("released", "completed")]
    [InlineData("ongoing", "watching")]
    public async Task LastWatchedEpisodeAutomaticallyCompletesOnlyReleasedTitles(string releaseStatus, string expected)
    {
        var repo = await Repository();
        await repo.Cache(7, [Title(1) with { Status = releaseStatus }]);
        var entry = new WatchEntry(1, "Title", "provider", "release", "12", 12, 95, DateTimeOffset.UtcNow, 100);
        Assert.False(await repo.PersistPlayback(7, entry, false));
        Assert.Empty(await repo.Pending(7));
        Assert.True(await repo.PersistPlayback(7, entry, true));
        var saved = Assert.Single(await repo.Library(7));
        Assert.Equal(12, saved.EpisodesWatched);
        Assert.Equal(expected, saved.UserStatus);
    }
    private sealed class Http : HttpMessageHandler
    {
        public string Query = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (r.RequestUri!.AbsolutePath == "/api/graphql")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":{"animes":[{"id":"1","poster":{"originalUrl":"https://example.org/real.jpg"}}]}}""") });
            Query = r.RequestUri!.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""[{"id":99,"name":"Top","score":"9.42"}]""") });
        }
    }
    [AvaloniaFact]
    public async Task ListRepairsPosterWithoutOpeningCardAndSyncPreservesIt()
    {
        var repo = await Repository(); var tokens = new Tokens();
        var missing = Title(1) with { Poster = "https://shikimori.io/assets/globals/missing_original.jpg" };
        await repo.Cache(7, [missing]);
        var api = new ShikimoriService(new(new Http()), new(), tokens);
        var vm = new MainViewModel(repo, api, new(repo, api), tokens, new(repo, new Launcher()), new ProviderRegistry([]), NullLogger<MainViewModel>.Instance);
        await vm.Initialize();
        vm.UserId = 7; await vm.Reload();
        await vm.PosterRefresh;
        Assert.Equal("https://example.org/real.jpg", Assert.Single(vm.Items).Poster);
        Assert.Null(vm.SelectedAnime);
        await repo.ReplaceRemote(7, [missing]);
        Assert.Equal("https://example.org/real.jpg", Assert.Single(await repo.Library(7)).Poster);
        var changed = missing with { EpisodesWatched = 5, Score = 8 };
        await repo.Change(7, missing, changed);
        await repo.SavePoster(7, 1, "https://example.org/new.jpg");
        var saved = Assert.Single(await repo.Library(7));
        Assert.Equal(5, saved.EpisodesWatched);
        Assert.Equal(8, saved.Score);
        Assert.Single(await repo.Pending(7));
    }
    private sealed class Launcher : IPlaybackLauncher
    {
        public Task Open(PlaybackRequest r, CancellationToken ct = default) => Task.CompletedTask;
    }
    [Fact]
    public async Task UnidentifiedTitleCannotEnterTheRemoteQueue()
    {
        var repo = await Repository();
        var missing = Title(0) with { RateId = null };
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Change(7, missing, missing with { EpisodesWatched = 2 }));
        Assert.Empty(await repo.Pending(7));
        var api = new ShikimoriService(new(new Http()), new(), new Tokens());
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.UpdateFields(7, missing, new(7, 0, "test", 2, "watching", 0, "planned")));
    }
    private sealed class Metadata : IMetadataProvider
    {
        public string MetadataId => "shikimori";
        public string MetadataName => "Shikimori";
        public Task<List<Anime>> Search(string query, CancellationToken ct = default) => Task.FromResult(new List<Anime> { Title(63409) with { Name = "Witch", Year = 2026, Kind = "tv" } });
        public Task<Anime> Details(long id, CancellationToken ct = default) => Task.FromResult(Title(id) with { Name = "Witch" });
        public Task<IReadOnlyList<AnimeRelation>> Relations(long id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AnimeRelation>>([]);
    }
    [Fact]
    public async Task SourceMatchIgnoresOldZeroIdCacheAndFindsRealIdentity()
    {
        var repo = await Repository();
        var unknown = new Anime { Name = "Witch", Year = 2026, Kind = "tv" };
        await repo.SaveSetting("schedule-map:source:release:anime", unknown);
        await repo.SaveSetting("schedule-map:source:release", unknown.Identity);
        var item = new ScheduleItem(unknown, 2, null) { ProviderId = "source", Release = new("release", "Witch", Year: 2026, Kind: "tv") };
        var found = await new ScheduleMatcher(new Metadata(), repo).Enrich(item, [unknown], default);
        Assert.Equal(63409, found.Anime.ShikimoriId);
        Assert.Equal(found.Anime.Identity, found.Release!.MetadataIdentity);
    }
    [AvaloniaFact]
    public async Task CollectionContainsAllStatusesAndSearchOpensRankedCatalogue()
    {
        var repo = await Repository();
        await repo.Cache(7, [Title(1), Title(2) with { UserStatus = "completed", Year = 1988 }]);
        var http = new Http(); var tokens = new Tokens();
        var api = new ShikimoriService(new(http), new(), tokens);
        var vm = new MainViewModel(repo, api, new(repo, api), tokens, new(repo, new Launcher()), new ProviderRegistry([]), NullLogger<MainViewModel>.Instance);
        await vm.Initialize();
        vm.UserId = 7; await vm.Reload();
        Assert.Equal("Моя коллекция", vm.PageTitle);
        Assert.Equal(2, vm.Items.Count);
        Assert.Contains("1988", vm.LibraryYears);
        vm.LibraryYear = "1988";
        Assert.Equal(2, Assert.Single(vm.Items).ShikimoriId);
        await vm.NavigateCommand.ExecuteAsync("Поиск");
        Assert.Contains("order=ranked", http.Query);
        Assert.Equal(99, Assert.Single(vm.Items).ShikimoriId);
        Assert.Equal(9.42, vm.Items[0].CommunityScore);
        vm.SelectedAnime = Title(1);
        vm.Score = 7; // Hydration must not enqueue a write.
        Assert.Empty(await repo.Pending(7));
        vm.SelectedRating = 8;
        await vm.RatingSaveTask;
        Assert.Equal(8, Assert.Single(await repo.Pending(7)).Score);
    }
    [AvaloniaFact]
    public async Task DetailHeadingUsesTitleAndCollapsesWithHearts()
    {
        var repo = await Repository(); var tokens = new Tokens();
        var api = new ShikimoriService(new(new Http()), new(), tokens);
        var vm = new MainViewModel(repo, api, new(repo, api), tokens, new(repo, new Launcher()), new ProviderRegistry([]), NullLogger<MainViewModel>.Instance);
        await vm.Initialize();
        vm.UserId = 7; await vm.Reload();
        await vm.NavigateCommand.ExecuteAsync("Аниме");
        vm.SelectedAnime = Title(1) with { Name = string.Join(" ", Enumerable.Repeat("Длинное название аниме", 8)) };
        vm.Settings.WizardVisible = false;
        var window = new AnimeApp.UI.MainWindow { DataContext = vm, Width = 1100, Height = 800 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var heading = window.FindControl<Avalonia.Controls.TextBlock>("PageHeading")!;
        Assert.Equal(vm.SelectedAnime.Title, heading.Text);
        Assert.Same(AnimeApp.UI.HeartTrimming.Instance, heading.TextTrimming);
        Assert.True(heading.TextLayout.TextLines[0].HasCollapsed);
        Assert.True(heading.Bounds.Width < window.Width);
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1100, 800)))
        {
            bitmap.Render(window);
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/identity-heading.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            bitmap.Save(output);
        }
        vm.SelectedAnime = Title(2) with { Name = "Короткое название" };
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("Короткое название", heading.Text);
        Assert.False(heading.TextLayout.TextLines[0].HasCollapsed);
        window.Close();
    }
}
