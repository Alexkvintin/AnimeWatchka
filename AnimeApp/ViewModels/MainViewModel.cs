using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using AnimeApp.Models;
using AnimeApp.Services;
using AnimeApp.Services.Database;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Shikimori;
using AnimeApp.Services.Playback;
using Microsoft.Extensions.Logging;

namespace AnimeApp.ViewModels;
public sealed partial class MainViewModel : ObservableObject
{
    private readonly LibraryRepository repository;
    private readonly IMetadataProvider api;
    private readonly SyncService sync;
    private readonly ITokenStore tokens;
    private readonly PlaybackService playback;
    private readonly ILogger<MainViewModel> logger;
    private List<Anime> library = [];
    private CancellationTokenSource? continuationLoading;
    private readonly Stack<(string Page, Anime? Anime, Anime[] Items)> navigation = new();
    private bool restoringNavigation;
    public bool CanGoBack => navigation.Count > 0;
    public Task BackgroundSync { get; private set; } = Task.CompletedTask;
    internal Task CardReady { get; private set; } = Task.CompletedTask;
    private readonly Dictionary<long, string> resolvedPosters = new();
    private readonly Dictionary<long, DateTimeOffset> posterAttempts = new();
    public Task PosterRefresh { get; private set; } = Task.CompletedTask;

    private async Task RefreshPosters(long user, long[] ids)
    {
        try
        {
            var posters = await api.Posters(ids);
            foreach (var (id, url) in posters)
            {
                resolvedPosters[id] = url;
                await repository.SavePoster(user, id, url);
                if (UserId != user) continue;
                var index = library.FindIndex(a => a.ShikimoriId == id);
                if (index >= 0) library[index] = library[index] with { Poster = url };
                for (var i = 0; i < Items.Count; i++)
                    if (Items[i].ShikimoriId == id) Items[i] = Items[i] with { Poster = url };
                if (SelectedAnime?.ShikimoriId == id && !PosterService.IsUsable(SelectedAnime.Poster))
                    SelectedAnime = SelectedAnime with { Poster = url };
            }
            if (UserId == user && posters.Count > 0) RebuildRows();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Poster metadata refresh: {Type}", ex.GetType().Name);
        }
    }

    private void RememberPage()
    {
        if (restoringNavigation)
            return;
        navigation.Push((Page, SelectedAnime, Items.ToArray()));
        OnPropertyChanged(nameof(CanGoBack));
    }

    [RelayCommand]
    private async Task GoBack()
    {
        if (!navigation.TryPop(out var previous))
            return;
        detailsLoading?.Cancel();
        searchLoading?.Cancel();
        restoringNavigation = true;
        try
        {
            if (previous.Page == "Аниме")
                await OpenAnime(previous.Anime);
            else
            {
                Page = previous.Page;
                Items.Clear();
                foreach (var item in previous.Items)
                    Items.Add(item);
                RebuildRows();
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
        finally
        {
            restoringNavigation = false;
            OnPropertyChanged(nameof(CanGoBack));
        }
    }

    public ObservableCollection<Anime[]> Rows { get; } = [];

    private int columns = 3;
    public FilterChoice[] SearchKinds { get; } = [new("", "Любой тип"), new("tv", "TV"), new("movie", "Movie"), new("ova", "OVA"), new("ona", "ONA"), new("special", "Special")];
    public FilterChoice[] SearchStatuses { get; } = [new("", "Любой статус"), new("anons", "Анонс"), new("ongoing", "Выходит"), new("released", "Вышло")];
    public FilterChoice[] SearchSeasons { get; } = [new("", "Любой сезон"), new("winter", "Зима"), new("spring", "Весна"), new("summer", "Лето"), new("fall", "Осень")];

    [ObservableProperty]
    private string yearFrom = "";
    [ObservableProperty]
    private string yearTo = "";
    [ObservableProperty]
    private FilterChoice? searchKind;
    [ObservableProperty]
    private FilterChoice? searchStatus;
    [ObservableProperty]
    private FilterChoice? searchSeason;
    [ObservableProperty]
    private string libraryYear = "Все";
    public ObservableCollection<string> LibraryYears { get; } = ["Все"];
    public bool IsSearch => Page == "Поиск";
    public bool ShowLibraryYear => IsLibrary && !IsSearch;

    partial void OnYearFromChanged(string value)
    {
        if (IsSearch)
            _ = SearchDebounced();
    }

    partial void OnYearToChanged(string value)
    {
        if (IsSearch)
            _ = SearchDebounced();
    }

    partial void OnSearchKindChanged(FilterChoice? value)
    {
        if (IsSearch)
            _ = SearchDebounced();
    }

    partial void OnSearchStatusChanged(FilterChoice? value)
    {
        if (IsSearch)
            _ = SearchDebounced();
    }

    partial void OnSearchSeasonChanged(FilterChoice? value)
    {
        if (IsSearch)
            _ = SearchDebounced();
    }

    partial void OnLibraryYearChanged(string value)
    {
        if (!IsSearch)
            ApplyFilter();
    }

    public void SetColumns(int count)
    {
        count = Math.Clamp(count, 1, 8);
        if (columns == count)
            return;
        columns = count;
        RebuildRows();
    }

    private void RebuildRows()
    {
        for (var i = 0; i < Items.Count; i++)
            if (!PosterService.IsUsable(Items[i].Poster) && resolvedPosters.TryGetValue(Items[i].ShikimoriId, out var poster))
                Items[i] = Items[i] with { Poster = poster };
        Rows.Clear();
        foreach (var row in Items.Chunk(columns))
            Rows.Add(row);
        var now = DateTimeOffset.UtcNow;
        var missing = Items.Where(a => a.ShikimoriId > 0 && !PosterService.IsUsable(a.Poster) && (!posterAttempts.TryGetValue(a.ShikimoriId, out var at) || now - at > TimeSpan.FromMinutes(5))).Select(a => a.ShikimoriId).Distinct().ToArray();
        if (missing.Length > 0)
        {
            foreach (var id in missing) posterAttempts[id] = now;
            PosterRefresh = RefreshPosters(UserId, missing);
        }
    }

    public AppearanceViewModel Appearance => AppearanceViewModel.Current;
    internal void ResetAccountView()
    {
        detailsLoading?.Cancel(); searchLoading?.Cancel(); continuationLoading?.Cancel(); Sources.CancelLoading();
        navigation.Clear(); SelectedAnime = null; Items.Clear(); Rows.Clear(); library.Clear();
        History.Clear(); HistoryItems.Clear(); Seasons.Clear(); Related.Clear();
        Sources.Episodes.Clear(); Sources.EpisodeTiles.Clear(); Sources.Releases.Clear();
        Page = "Главная"; OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(EmptyText));
    }
    internal long UserId;
    public ObservableCollection<AnimeRelation> Seasons { get; } = [];
    public ObservableCollection<AnimeRelation> Related { get; } = [];

    [ObservableProperty]
    private int score;
    public int SelectedRating
    {
        get => Score;
        set
        {
            if (value == Score || value is < 0 or > 10 || !CanEditProgress || SelectedAnime is null) return;
            Score = value;
            RatingSaveTask = SaveRating(SelectedAnime, UserId, value);
        }
    }
    public Task RatingSaveTask { get; private set; } = Task.CompletedTask;
    private readonly SemaphoreSlim ratingGate = new(1, 1);
    partial void OnScoreChanged(int value) => OnPropertyChanged(nameof(SelectedRating));
    private async Task SaveRating(Anime anime, long user, int value)
    {
        await ratingGate.WaitAsync();
        try
        {
            var current = (await repository.Library(user)).FirstOrDefault(a => a.ShikimoriId == anime.ShikimoriId) ?? anime;
            var updated = current with { Score = value };
            await repository.Change(user, current, updated);
            if (UserId == user)
            {
                var index = library.FindIndex(a => a.ShikimoriId == anime.ShikimoriId);
                if (index >= 0) library[index] = updated;
                else library.Add(updated);
                if (SelectedAnime?.ShikimoriId == anime.ShikimoriId && Score == value)
                    SelectedAnime = SelectedAnime with { Score = value };
            }
            if (UserId == user) PendingCount = (await repository.Pending(user)).Count;
            if (user > 0 && Settings.AutoSync)
            {
                var result = await sync.SendPending(user);
                if (UserId == user) Message = result;
            }
            if (UserId == user) PendingCount = (await repository.Pending(user)).Count;
        }
        catch (Exception ex) { ReportError(ex); }
        finally { ratingGate.Release(); }
    }
    [ObservableProperty]
    private string relationsMessage = "";
    public int[] Scores { get; } = Enumerable.Range(0, 11).ToArray();

    private CancellationTokenSource? searchLoading;
    private CancellationTokenSource? detailsLoading;
    public ObservableCollection<Anime> Items { get; } = [];
    public ObservableCollection<HistoryItem> HistoryItems { get; } = [];
    public ScheduleViewModel Schedule { get; }
    public bool IsSchedule => Page == "Онгоинги";
    public bool IsHome => Page == "Главная";
    public ObservableCollection<WatchEntry> History { get; } = [];
    public string[] UserStatuses { get; } = ["watching", "planned", "completed", "on_hold", "dropped", "rewatching"];

    [ObservableProperty]
    private string page = "Главная";
    [ObservableProperty]
    private string message = "";
    [ObservableProperty]
    private bool isBusy;
    [ObservableProperty]
    private string query = "";
    [ObservableProperty]
    private Anime? selectedAnime;
    [ObservableProperty]
    private int watched;
    [ObservableProperty]
    private string userStatus = "planned";
    [ObservableProperty]
    private string account = "Подключить Shikimori";
    [ObservableProperty]
    private string accountAvatar = "";
    public bool HasAccountAvatar => !string.IsNullOrWhiteSpace(AccountAvatar);
    partial void OnAccountAvatarChanged(string value) => OnPropertyChanged(nameof(HasAccountAvatar));

    [ObservableProperty]
    private int pendingCount;
    public bool IsLibrary => Page != "Настройки" && Page != "История" && Page != "Аниме" && !IsSchedule;
    public bool IsSettings => Page == "Настройки";
    public bool IsHistory => Page == "История";
    public bool IsDetails => Page == "Аниме";
    public string PageTitle => IsHome ? "Моя коллекция" : IsDetails ? SelectedAnime?.Title ?? "Аниме" : Page;
    public bool CanEditProgress => SelectedAnime is { ShikimoriId: > 0 };
    partial void OnSelectedAnimeChanged(Anime? value)
    {
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(CanEditProgress));
    }
    public string Subtitle => IsHome ? "Все тайтлы из вашего списка Shikimori · любой статус" : IsSearch ? "Каталог Shikimori · по рейтингу" : ShowLibraryYear ? "Ваш список Shikimori · статус: " + Page : "";
    public string EmptyText => UserId == 0 && !IsSearch ? "Подключите Shikimori, чтобы увидеть свою коллекцию." : Page == "Поиск" ? "По этим фильтрам ничего не найдено. Попробуйте другой год или название." : "В этом разделе пока нет аниме с выбранным годом. Выберите «Все» или откройте каталог.";
    public bool IsEmpty => Items.Count == 0;
    public SettingsViewModel Settings { get; }
    public SourcesViewModel Sources { get; }

    public MainViewModel(LibraryRepository repository, ShikimoriService api, SyncService sync, ITokenStore tokens, PlaybackService playback, ProviderRegistry providers, ILogger<MainViewModel> logger)
    {
        this.repository = repository;
        this.api = api;
        this.sync = sync;
        this.tokens = tokens;
        this.playback = playback;
        this.logger = logger;
        Sources = new(this, repository, playback, providers);
        Settings = new(this, repository, api, sync, tokens);
        Schedule = new(api, repository, Sources);
    }

    internal void ReportError(Exception ex)
    {
        logger.LogWarning(ex, "Source operation failed");
        Message = ex.Message;
    }

    internal async Task Run(Func<Task> action)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Operation failed");
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task Initialize() => Run(async () =>
    {
        await Settings.Initialize();
        var savedSort = await repository.Setting<string>("library-sort");
        if (LibrarySorts.Contains(savedSort ?? "")) LibrarySort = savedSort!;
        await Reload();
        if (UserId > 0) _ = Settings.RefreshIdentity();
        if (UserId > 0 && Settings.AutoSync && BackgroundSync.IsCompleted)
            BackgroundSync = RefreshLibraryInBackground(UserId);
    });
    private async Task RefreshLibraryInBackground(long user)
    {
        try
        {
            var last = await repository.Setting<DateTimeOffset>("library-sync:" + user);
            if (PendingCount == 0 && library.Count > 0 && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(30))
                return;
            await sync.Synchronize(user);
            if (UserId == user)
                await Reload();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Background library refresh: {Type}", ex.GetType().Name);
        }
    }

    public Task RetryPending() => !IsBusy && UserId > 0 && Settings.AutoSync && PendingCount > 0 ? Run(async () => { Message = await sync.SendPending(UserId); await Reload(); }) : Task.CompletedTask;
    internal async Task Reload()
    {
        var user = UserId;
        var snapshot = user > 0 ? await repository.Library(user) : [];
        if (UserId != user) return;
        var adultEnabled = Settings.ShowAdultContent;
        var permittedSnapshot = await api.FilterContent(snapshot);
        if (UserId != user || adultEnabled != Settings.ShowAdultContent) return;
        library = permittedSnapshot.ToList();
        var previousYear = LibraryYear;
        LibraryYears.Clear();
        LibraryYears.Add("Все");
        foreach (var year in library.Where(a => a.Year.HasValue).Select(a => a.Year!.Value).Distinct().OrderDescending())
            LibraryYears.Add(year.ToString());
        LibraryYear = LibraryYears.Contains(previousYear) ? previousYear : "Все";
        Settings.UnlinkedProgress.Clear();
        if (UserId > 0 && Settings.ShowAdultContent)
            foreach (var entry in await repository.Unlinked(UserId)) Settings.UnlinkedProgress.Add(entry);
        PendingCount = UserId > 0 ? (await repository.Pending(UserId)).Count : 0;
        Settings.RefreshConflicts();
        History.Clear();
        foreach (var e in UserId > 0 ? await repository.History(UserId) : [])
            if (Settings.ShowAdultContent || library.Any(a => a.ShikimoriId == e.AnimeId))
            History.Add(e);
        HistoryItems.Clear();
        foreach (var e in History)
            HistoryItems.Add(new(e, library.FirstOrDefault(a => a.ShikimoriId == e.AnimeId)?.Poster ?? "", Sources.Providers.FirstOrDefault(p => p.Id == e.ProviderId)?.Name ?? e.ProviderId));
        continuationLoading?.Cancel();
        continuationLoading = new();
        _ = LoadContinuation(library, continuationLoading.Token);
        if (IsSearch)
            _ = Search();
        else
            ApplyFilter();
        if (SelectedAnime is not null && library.FirstOrDefault(a => a.ShikimoriId == SelectedAnime.ShikimoriId)is Anime refreshed)
        {
            SelectedAnime = refreshed;
            Watched = refreshed.EpisodesWatched;
            UserStatus = refreshed.UserStatus;
            Score = refreshed.Score;
            Sources.RefreshTiles();
        }
    }

    private async Task LoadContinuation(List<Anime> snapshot, CancellationToken ct)
    {
        var service = new ContinuationService(repository);
        var providers = Sources.EnabledProviders.Select(p => p.Provider).ToArray();
        var history = History.ToArray();
        try
        {
            foreach (var a in snapshot.Where(a => a.UserStatus is "watching" or "rewatching").ToArray())
            {
                ct.ThrowIfCancellationRequested();
                var plan = await service.Find(a, history, providers, ct);
                ct.ThrowIfCancellationRequested();
                var updated = a with
                {
                    Continuation = plan
                };
                snapshot[snapshot.IndexOf(a)] = updated;
                if (Page == "Главная" && Items.IndexOf(a)is var index && index >= 0)
                {
                    Items[index] = updated;
                    var rowIndex = index / columns;
                    var row = (Anime[])Rows[rowIndex].Clone();
                    row[index % columns] = updated;
                    Rows[rowIndex] = row;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Continuation unavailable");
        }
    }

    private void ApplyFilter()
    {
        Items.Clear();
        var status = Page switch
        {
            "Смотрю" => "watching",
            "Запланировано" => "planned",
            "Завершено" => "completed",
            "Отложено" => "on_hold",
            "Брошено" => "dropped",
            "Пересматриваю" => "rewatching",
            _ => null
        };
        IEnumerable<Anime> items = library;
        if (int.TryParse(LibraryYear, out var year))
            items = items.Where(a => a.Year == year);
        if (status is not null)
            items = items.Where(a => a.UserStatus == status);
        items = LibrarySort switch
        {
            "Недавно добавленные" => items.OrderByDescending(a => a.AddedAt).ThenBy(a => a.Title),
            "По названию" => items.OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase),
            "По моей оценке" => items.OrderByDescending(a => a.Score).ThenBy(a => a.Title),
            _ => items.OrderByDescending(a => History.Where(h => h.AnimeId == a.ShikimoriId).Select(h => (DateTimeOffset?)h.UpdatedAt).Max()).ThenBy(a => a.Title)
        };
        foreach (var a in items)
            Items.Add(a);
        RebuildRows();
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnPageChanged(string value)
    {
        OnPropertyChanged(nameof(LumiImage));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(IsSearch));
        OnPropertyChanged(nameof(ShowLibraryYear));
        OnPropertyChanged(nameof(IsHome));
        OnPropertyChanged(nameof(IsSchedule));
        OnPropertyChanged(nameof(IsLibrary));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsHistory));
        OnPropertyChanged(nameof(IsDetails));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(EmptyText));
    }

    [RelayCommand]
    private async Task Navigate(string? page)
    {
        if (Page != page)
            RememberPage();
        detailsLoading?.Cancel();
        Sources.CancelLoading();
        IsBusy = false;
        searchLoading?.Cancel();
        Page = page ?? "Главная";
        if (IsSchedule)
            _ = Schedule.Refresh();
        if (IsSearch)
            await Search();
        else
            ApplyFilter();
        if (SelectedAnime is not null && library.FirstOrDefault(a => a.ShikimoriId == SelectedAnime.ShikimoriId)is Anime refreshed)
        {
            SelectedAnime = refreshed;
            Watched = refreshed.EpisodesWatched;
            UserStatus = refreshed.UserStatus;
            Score = refreshed.Score;
            Sources.RefreshTiles();
        }
    }

    [RelayCommand]
    private async Task OpenCatalog()
    {
        RememberPage();
        YearFrom = LibraryYear == "Все" ? DateTime.Now.Year.ToString() : LibraryYear;
        YearTo = "";
        Query = "";
        await Search();
    }

    [RelayCommand]
    private async Task ManageAccount()
    {
        if (UserId > 0)
            await Navigate("Настройки");
        else
        {
            await Navigate("Настройки");
            await Settings.AuthorizeCommand.ExecuteAsync(null);
        }
    }

    partial void OnQueryChanged(string value)
    {
        if (Page == "Поиск")
            _ = SearchDebounced();
    }

    private async Task SearchDebounced()
    {
        searchLoading?.Cancel();
        searchLoading = new();
        var ct = searchLoading.Token;
        try
        {
            await Task.Delay(400, ct);
            await SearchCore(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Search()
    {
        searchLoading?.Cancel();
        searchLoading = new();
        try
        {
            await SearchCore(searchLoading.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private async Task SearchCore(CancellationToken ct)
    {
        detailsLoading?.Cancel();
        IsBusy = false;
        Page = "Поиск";
        static int? Year(string text) => string.IsNullOrWhiteSpace(text) ? null : int.TryParse(text, out var y) ? y : throw new ArgumentException("Год должен быть числом.");
        var filters = new AnimeSearch(Query, Year(YearFrom), Year(YearTo), SearchKind?.Id ?? "", SearchStatus?.Id ?? "", SearchSeason?.Id ?? "");
        filters.Validate();
        var found = await api.Search(filters, ct);
        ct.ThrowIfCancellationRequested();
        Items.Clear();
        foreach (var a in found)
            Items.Add(library.FirstOrDefault(x => x.ShikimoriId == a.ShikimoriId) is Anime owned ? owned with { CommunityScore = a.CommunityScore } : a);
        RebuildRows();
        OnPropertyChanged(nameof(IsEmpty));
        Message = $"Найдено: {Items.Count} (до 50 результатов). " + (api.SupportsSearchFilters ? "Фильтры применены на сервере." : "Источник не поддерживает серверные фильтры: локальная фильтрация ограниченной выборки.");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenAnime(Anime? anime) => await OpenAnimeCore(anime);
    private async Task OpenAnimeCore(Anime? anime, string? providerId = null, ProviderRelease? release = null)
    {
        if (anime is null)
            return;
        if ((await api.FilterContent([anime])).Count == 0)
        {
            Message = "Тайтл скрыт настройкой «Отображать 18+» или его возрастной рейтинг не подтверждён.";
            return;
        }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CardReady = ready.Task;
        RememberPage();
        searchLoading?.Cancel();
        detailsLoading?.Cancel();
        detailsLoading = new();
        var ct = detailsLoading.Token;
        IsBusy = false;
        try
        {
            SelectedAnime = anime;
            Watched = anime.EpisodesWatched;
            UserStatus = anime.UserStatus;
            Score = anime.Score;
            Seasons.Clear();
            Related.Clear();
            RelationsMessage = "";
            Page = "Аниме";
            Sources.SourceQuery = anime.Title;
            Sources.Episodes.Clear();
            Sources.Releases.Clear();
            Sources.SelectedRelease = null;
            Sources.SelectedEpisode = null;
            var sourceTask = release is not null && providerId is not null ? Sources.UseRelease(providerId, release, ct) : Task.CompletedTask;
            try
            {
                if (anime.ShikimoriId <= 0)
                {
                    await sourceTask;
                    if (release is null || providerId is null) return;
                    var matched = await new ScheduleMatcher(api, repository).Enrich(new(anime, 0, null) { ProviderId = providerId, Release = release }, library, ct);
                    ct.ThrowIfCancellationRequested();
                    if (matched.Anime.ShikimoriId <= 0) return;
                    anime = matched.Anime;
                    SelectedAnime = anime with { Poster = release.Poster == "" ? anime.Poster : release.Poster };
                    Watched = anime.EpisodesWatched;
                    UserStatus = anime.UserStatus;
                    Score = anime.Score;
                    await repository.SaveMapping(new(anime.ShikimoriId, providerId, release.Id), ct);
                    Sources.RefreshTiles();
                }

                var details = await api.Details(anime.Identity, ct);
                ct.ThrowIfCancellationRequested();
                var sourcePoster = SelectedAnime?.Poster;
                SelectedAnime = details with
                {
                    EpisodesWatched = anime.EpisodesWatched,
                    UserStatus = anime.UserStatus,
                    RateId = anime.RateId,
                    AddedAt = anime.AddedAt,
                    Score = anime.Score,
                    Rewatches = anime.Rewatches
                };
                if (!string.IsNullOrEmpty(sourcePoster) && !sourcePoster.Contains("missing_"))
                    SelectedAnime = SelectedAnime with
                    {
                        Poster = sourcePoster
                    };
                if (library.Any(a => a.ShikimoriId == anime.ShikimoriId))
                    await repository.Cache(UserId, [SelectedAnime]);
            }
            catch (IOException ex)
            {
                Message = "Показаны сохранённые данные. " + ex.Message;
                logger.LogWarning(ex, "Details unavailable");
            }

            try
            {
                var relations = await api.Relations(anime.Identity, ct);
                ct.ThrowIfCancellationRequested();
                var permitted = (await api.FilterContent(relations.Select(r => r.Anime), ct)).Select(a => a.Identity).ToHashSet();
                foreach (var r in relations.Where(r => permitted.Contains(r.Anime.Identity)))
                    (r.IsSeason ? Seasons : Related).Add(r);
            }
            catch (IOException)
            {
                RelationsMessage = "Связанные сезоны временно недоступны.";
            }

            ct.ThrowIfCancellationRequested();
            await sourceTask;
            if (release is null || providerId is null) await Sources.SelectForAnime(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                ReportError(ex);
        }
        finally
        {
            ready.TrySetResult();
            if (!ct.IsCancellationRequested)
                IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenSchedule(ScheduleItem? item) => Run(async () =>
    {
        if (item is null)
            return;
        if (item.Release is null)
        {
            await OpenRelated(item.Anime);
            return;
        }

        var provider = Sources.Providers.FirstOrDefault(p => p.Enabled && p.Id == item.ProviderId);
        if (provider is null)
        {
            Message = "Источник отключён. Включите его в настройках.";
            return;
        }

        var card = library.FirstOrDefault(a => item.Anime.ShikimoriId > 0 && a.Identity == item.Identity) ?? item.Anime;
        if (!string.IsNullOrEmpty(item.Poster))
            card = card with
            {
                Poster = item.Poster
            };
        await OpenAnimeCore(card, provider.Id, item.Release);
        return;
    });
    [RelayCommand]
    private Task OpenRelated(Anime? anime) => OpenAnime(anime is null ? null : library.FirstOrDefault(a => a.ShikimoriId == anime.ShikimoriId) ?? anime);
    [RelayCommand]
    private Task SaveScore() => Run(async () =>
    {
        if (!CanEditProgress || SelectedAnime is null)
            return;
        if (Score is < 0 or > 10)
            throw new InvalidOperationException("Оценка должна быть от 0 до 10.");
        var current = (await repository.Library(UserId)).FirstOrDefault(a => a.ShikimoriId == SelectedAnime.ShikimoriId) ?? SelectedAnime;
        await repository.Change(UserId, current, current with { Score = Score });
        Message = "Оценка сохранена локально.";
        if (Settings.AutoSync && UserId > 0)
            Message = await sync.SendPending(UserId);
        await Reload();
    });
    [RelayCommand]
    private Task SaveProgress() => Run(async () =>
    {
        if (!CanEditProgress || SelectedAnime is null)
            return;
        if (Watched < 0 || (SelectedAnime.EpisodesTotal > 0 && Watched > SelectedAnime.EpisodesTotal))
            throw new InvalidOperationException("Число просмотренных серий выходит за пределы тайтла.");
        if (!UserStatuses.Contains(UserStatus))
            throw new InvalidOperationException("Выберите статус из списка.");
        var before = library.FirstOrDefault(x => x.ShikimoriId == SelectedAnime.ShikimoriId) ?? SelectedAnime;
        SelectedAnime = SelectedAnime with
        {
            EpisodesWatched = Watched,
            UserStatus = UserStatus
        };
        await repository.Change(UserId, before, SelectedAnime);
        Message = "Прогресс сохранён локально.";
        await Reload();
        if (Settings.AutoSync && UserId > 0)
            Message = await sync.SendPending(UserId);
        await Reload();
    });
    [RelayCommand]
    private async Task OpenHistory(WatchEntry? entry)
    {
        if (entry is null)
            return;
        if (entry.AnimeId == 0)
        {
            var web = Sources.Providers.FirstOrDefault(p => p.Id == entry.ProviderId && p.IsWeb && p.Enabled);
            if (web is not null)
                await Run(() => Sources.OpenWeb(web));
            else
                await Run(async () =>
                {
                    var p = Sources.Providers.FirstOrDefault(p => p.Id == entry.ProviderId && p.Enabled);
                    if (p is null)
                    {
                        Message = "Источник истории отключён.";
                        return;
                    }

                    var episodes = await p.Provider.GetEpisodes(entry.ReleaseId);
                    var episode = episodes.FirstOrDefault(e => e.Id == entry.EpisodeId && e.Available);
                    if (episode is null)
                    {
                        Message = "Серия больше не доступна.";
                        return;
                    }

                    var source = await p.Provider.GetPlayback(entry.ReleaseId, episode);
                    await playback.Start(new(UserId, 0, entry.Title, p.Id, entry.ReleaseId, episode.Id, episode.Number, source, entry.Position, CountsTowardProgress: false));
                });
            return;
        }

        await OpenAnime(library.FirstOrDefault(a => a.ShikimoriId == entry.AnimeId));
        var selected = false;
        await Run(async () =>
        {
            if (SelectedAnime?.ShikimoriId == entry.AnimeId)
                selected = await Sources.SelectSaved(entry.ProviderId, entry.ReleaseId, entry.EpisodeId);
        });
        if (selected)
            await Sources.PlayCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task ContinueAnime(Anime? anime)
    {
        if (anime?.Continuation is not { Available: true } plan)
            return;
        await OpenAnime(anime);
        var selected = false;
        await Run(async () => selected = await Sources.SelectSaved(plan.ProviderId, plan.ReleaseId, plan.Episode!.Id));
        if (selected)
            await Sources.PlayCommand.ExecuteAsync(null);
    }
}
