using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using AnimeApp.Models;
using AnimeApp.Services.Database;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Playback;

namespace AnimeApp.ViewModels;
public sealed record EpisodeTile(ProviderEpisode Episode, string Label, string Hint, bool Watched, bool Current, bool Partial, string ResumeText = "");
public sealed partial class SourcesViewModel : ObservableObject
{
    private readonly MainViewModel host;
    public long UserId => host.UserId;

    private readonly LibraryRepository repository;
    private readonly PlaybackService playback;
    private readonly ReleaseMatcher matcher = new();
    private CancellationTokenSource? loading;
    private bool selectingSaved;
    public void CancelLoading() => loading?.Cancel();
    public async Task UseRelease(string providerId, ProviderRelease release, CancellationToken ct = default)
    {
        loading?.Cancel();
        var provider = EnabledProviders.FirstOrDefault(p => p.Id == providerId);
        if (provider is null)
            return;
        selectingSaved = true;
        SelectedProvider = provider;
        selectingSaved = false;
        SelectedRelease = release;
        IsChoosing = false;
        ShowCandidates = false;
        SourceMessage = "";
        if (!string.IsNullOrEmpty(release.Poster) && host.SelectedAnime is { } anime)
            host.SelectedAnime = anime with
            {
                Poster = release.Poster
            };
        await LoadEpisodesCore(ct);
        ct.ThrowIfCancellationRequested();
        if (host.SelectedAnime is { ShikimoriId: > 0 } selected && SelectedRelease == release)
        {
            await repository.SaveMapping(new(selected.ShikimoriId, providerId, release.Id));
            IsLinked = true;
        }
    }

    private readonly ProviderRegistry registry;
    public string PreferredProviderId { get; set; } = "";
    public string PreferredLanguage { get; set; } = "uk,ru,any";

    public void Register(IAnimeProvider provider)
    {
        registry.Register(provider);
        if (!Providers.Any(p => p.Id == provider.Id))
            Providers.Add(new(provider));
    }

    public Task OpenWeb(ProviderViewModel p) => playback.Start(new(host.UserId, 0, p.Name, p.Id, p.Id, "web", 0, new(PlaybackKind.WebPage, p.Provider.Url), CountsTowardProgress: false));
    public SourcesViewModel(MainViewModel host, LibraryRepository repository, PlaybackService playback, ProviderRegistry registry)
    {
        this.registry = registry;
        this.host = host;
        this.repository = repository;
        this.playback = playback;
        foreach (var p in registry.All)
            Providers.Add(new(p));
    }

    public ObservableCollection<ProviderViewModel> Providers { get; } = [];
    public ObservableCollection<ProviderViewModel> EnabledProviders { get; } = [];
    public ObservableCollection<ProviderRelease> Releases { get; } = [];
    public ObservableCollection<ProviderEpisode> Episodes { get; } = [];
    public ObservableCollection<EpisodeTile> EpisodeTiles { get; } = [];
    public ObservableCollection<ReleaseSuggestion> Suggestions { get; } = [];

    [ObservableProperty]
    private ProviderViewModel? selectedProvider;
    [ObservableProperty]
    private ProviderRelease? selectedRelease;
    partial void OnSelectedReleaseChanged(ProviderRelease? value)
    {
        if (!string.IsNullOrEmpty(value?.Poster) && host.SelectedAnime is { } anime)
            host.SelectedAnime = anime with
            {
                Poster = value.Poster
            };
    }

    [ObservableProperty]
    private ProviderEpisode? selectedEpisode;
    [ObservableProperty]
    private ReleaseSuggestion? selectedSuggestion;
    [ObservableProperty]
    private string sourceQuery = "";
    [ObservableProperty]
    private string sourceMessage = "";
    [ObservableProperty]
    private bool showCandidates = true;
    [ObservableProperty]
    private bool isLinked;
    [ObservableProperty]
    private bool isChoosing = true;
    partial void OnSelectedSuggestionChanged(ReleaseSuggestion? value)
    {
        if (value is not null)
            SelectedRelease = value.Release;
    }

    partial void OnSelectedEpisodeChanged(ProviderEpisode? value) => RefreshTiles();
    public void RefreshProviders()
    {
        EnabledProviders.Clear();
        foreach (var p in Providers.Where(p => p.Enabled && !p.IsWeb).OrderBy(p => p.Priority).ThenBy(p => LanguageRank(p.Language)))
            EnabledProviders.Add(p);
        SelectedProvider = EnabledProviders.FirstOrDefault();
    }

    public int LanguageRank(string language)
    {
        var languages = PreferredLanguage.Split(',', StringSplitOptions.TrimEntries);
        var i = Array.IndexOf(languages, language);
        return i < 0 ? languages.Length : i;
    }

    partial void OnSelectedProviderChanged(ProviderViewModel? value)
    {
        if (!selectingSaved && host.SelectedAnime is not null)
            _ = InitializeSafe();
    }

    private async Task InitializeSafe()
    {
        try
        {
            await InitializeForAnime();
        }
        catch (OperationCanceledException)
        {
            SourceMessage = "Поиск отменён";
        }
        catch (Exception ex)
        {
            host.ReportError(ex);
            SourceMessage = ex.Message;
        }
    }

    private readonly Dictionary<(long Anime, string Provider), (DateTimeOffset At, IReadOnlyList<ReleaseSuggestion> Suggestions)> suggestionCache = new();
    private async Task<IReadOnlyList<ReleaseSuggestion>> SuggestionsFor(IAnimeProvider provider, Anime anime, CancellationToken ct)
    {
        var key = (anime.ShikimoriId, provider.Id);
        if (suggestionCache.TryGetValue(key, out var saved) && DateTimeOffset.UtcNow - saved.At < TimeSpan.FromMinutes(3))
            return saved.Suggestions;
        var result = await matcher.Suggest(provider, anime, ct);
        ct.ThrowIfCancellationRequested();
        if (suggestionCache.Count > 100)
            suggestionCache.Clear();
        suggestionCache[key] = (DateTimeOffset.UtcNow, result);
        return result;
    }

    public async Task SelectForAnime(CancellationToken ct = default)
    {
        if (host.SelectedAnime is null)
            return;
        foreach (var p in EnabledProviders)
            if (await repository.Mapping(host.SelectedAnime.ShikimoriId, p.Id, ct)is not null)
            {
                selectingSaved = true;
                SelectedProvider = p;
                selectingSaved = false;
                try
                {
                    await InitializeForAnime(ct);
                    if (Episodes.Any(e => e.Available))
                        return;
                }
                catch (IOException)
                {
                    SourceMessage = "Сохранённый источник недоступен; проверяем другие источники…";
                }
            }

        var anime = host.SelectedAnime;
        ProviderViewModel? preferred = null;
        foreach (var p in EnabledProviders)
        {
            ct.ThrowIfCancellationRequested();
            if (!p.Provider.Features.HasFlag(ProviderCapabilities.Search))
                continue;
            SourceMessage = $"Проверяем релиз и доступные серии: {p.Name}…";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var candidates = await SuggestionsFor(p.Provider, anime, timeout.Token);
                foreach (var candidate in candidates.Where(c => c == ReleaseMatcher.Unambiguous(candidates)))
                    if ((await p.Provider.GetEpisodes(candidate.Release.Id, timeout.Token)).Any(e => e.Available))
                    {
                        preferred = p;
                        break;
                    }

                if (preferred is not null)
                    break;
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException)when (!ct.IsCancellationRequested)
            {
            }
        }

        ct.ThrowIfCancellationRequested();
        if (host.SelectedAnime?.Identity != anime.Identity)
            return;
        selectingSaved = true;
        SelectedProvider = preferred ?? EnabledProviders.FirstOrDefault();
        selectingSaved = false;
        await InitializeForAnime(ct);
        if (preferred is null && SelectedProvider is not null)
            SourceMessage = "В этой студии не найден подтверждённый релиз. Выберите другую студию.";
    }

    public async Task InitializeForAnime(CancellationToken external = default)
    {
        loading?.Cancel();
        loading?.Dispose();
        loading = CancellationTokenSource.CreateLinkedTokenSource(external);
        var ct = loading.Token;
        var anime = host.SelectedAnime;
        var provider = SelectedProvider;
        Releases.Clear();
        Episodes.Clear();
        EpisodeTiles.Clear();
        Suggestions.Clear();
        SelectedRelease = null;
        SelectedEpisode = null;
        IsLinked = false;
        IsChoosing = false;
        ShowCandidates = true;
        if (anime is null || provider is null)
            return;
        SourceQuery = anime.Title;
        SourceMessage = "Ищем сохранённый релиз…";
        var id = await repository.Mapping(anime.ShikimoriId, provider.Id, ct);
        if (id is not null)
        {
            IsLinked = true;
            var release = await provider.Provider.GetRelease(id, ct);
            ct.ThrowIfCancellationRequested();
            SelectedRelease = release;
            Releases.Add(SelectedRelease);
            await LoadEpisodesCore(ct);
            IsLinked = true;
            IsChoosing = false;
            SourceMessage = $"Доступно серий: {Episodes.Count}";
        }
        else
        {
            SourceMessage = "Подбираем серии…";
            foreach (var s in await SuggestionsFor(provider.Provider, anime, ct))
            {
                ct.ThrowIfCancellationRequested();
                Suggestions.Add(s);
                Releases.Add(s.Release);
            }

            SelectedSuggestion = Suggestions.FirstOrDefault();
            ShowCandidates = ReleaseMatcher.Unambiguous(Suggestions.ToArray()) is null;
            if (!ShowCandidates && SelectedSuggestion is { Score: >= 0.85 } best)
            {
                var release = await provider.Provider.GetRelease(best.Release.Id, ct);
                ct.ThrowIfCancellationRequested();
                SelectedRelease = release;
                await LoadEpisodesCore(ct);
                await repository.SaveMapping(new(anime.ShikimoriId, provider.Id, release.Id), ct);
                IsLinked = true;
                IsChoosing = false;
                SourceMessage = "";
            }
            else
            {
                SelectedRelease = null;
                IsChoosing = false;
                SourceMessage = "В этой студии не найден однозначно подходящий релиз. Выберите другую студию.";
            }
        }
    }

    public async Task<bool> SelectSaved(string providerId, string releaseId, string episodeId)
    {
        var provider = EnabledProviders.FirstOrDefault(p => p.Id == providerId);
        if (provider is null)
        {
            host.Message = "Источник этой серии отключён. Включите его в настройках.";
            return false;
        }

        // Select without starting the automatic search in the property hook.
        selectingSaved = true;
        try
        {
            SelectedProvider = provider;
        }
        finally
        {
            selectingSaved = false;
        }

        loading?.Cancel();
        SelectedRelease = await provider.Provider.GetRelease(releaseId);
        await LoadEpisodesCore();
        SelectedEpisode = Episodes.FirstOrDefault(e => e.Id == episodeId && e.Available);
        IsLinked = await repository.Mapping(host.SelectedAnime!.ShikimoriId, providerId) == releaseId;
        IsChoosing = false;
        SourceMessage = IsLinked ? "Выбрана сохранённая серия" : "Релиз из истории. Текущая связь тайтла не изменена.";
        if (SelectedEpisode is null)
        {
            host.Message = "Эта серия больше не доступна в источнике. Выберите доступную серию.";
            return false;
        }

        return true;
    }

    [RelayCommand]
    private Task SearchSource() => host.Run(async () =>
    {
        if (SelectedProvider is null)
            throw new InvalidOperationException("Включите источник в настройках.");
        loading?.Cancel();
        IsChoosing = true;
        ShowCandidates = true;
        Releases.Clear();
        Suggestions.Clear();
        SelectedRelease = null;
        foreach (var r in await SelectedProvider.Provider.Search(SourceQuery))
            Releases.Add(r);
        SourceMessage = Releases.Count > 0 ? "Выберите релиз и нажмите «Использовать»." : "Релизы не найдены.";
    });
    [RelayCommand]
    private Task LinkSource() => host.Run(async () =>
    {
        if (host.SelectedAnime is null || SelectedProvider is null || SelectedRelease is null)
            throw new InvalidOperationException("Выберите релиз.");
        SelectedRelease = await SelectedProvider.Provider.GetRelease(SelectedRelease.Id);
        await LoadEpisodesCore();
        await repository.SaveMapping(new(host.SelectedAnime.ShikimoriId, SelectedProvider.Id, SelectedRelease.Id));
        IsLinked = true;
        IsChoosing = false;
        SourceMessage = $"Релиз связан · доступно серий: {Episodes.Count}";
    });
    [RelayCommand]
    private void ChangeRelease()
    {
        IsChoosing = true;
        ShowCandidates = true;
        SourceMessage = "Выберите другой релиз. Текущая связь заменится после подтверждения.";
    }

    [RelayCommand]
    private Task RemoveMapping() => host.Run(async () =>
    {
        if (host.SelectedAnime is null || SelectedProvider is null)
            return;
        loading?.Cancel();
        await repository.DeleteMapping(host.SelectedAnime.ShikimoriId, SelectedProvider.Id);
        SelectedRelease = null;
        SelectedEpisode = null;
        Episodes.Clear();
        EpisodeTiles.Clear();
        IsLinked = false;
        IsChoosing = true;
        SourceMessage = "Связь удалена.";
    });
    public async Task LoadEpisodesCore(CancellationToken ct = default)
    {
        Episodes.Clear();
        if (SelectedProvider is null || SelectedRelease is null)
            return;
        var provider = SelectedProvider;
        var release = SelectedRelease;
        var anime = host.SelectedAnime;
        var episodes = await provider.Provider.GetEpisodes(release.Id, ct);
        var history = await repository.History(host.UserId, ct);
        ct.ThrowIfCancellationRequested();
        if (SelectedProvider != provider || SelectedRelease != release || host.SelectedAnime?.Identity != anime?.Identity)
            return;
        foreach (var e in episodes)
            Episodes.Add(e);
        var resume = history.FirstOrDefault(h => h.AnimeId == anime?.ShikimoriId && h.ProviderId == provider.Id && h.ReleaseId == release.Id && !h.Completed && h.Position > 0);
        SelectedEpisode = Episodes.FirstOrDefault(e => e.Id == resume?.EpisodeId) ?? Episodes.FirstOrDefault(e => e.Number > host.Watched) ?? Episodes.FirstOrDefault();
        await RefreshDownloads();
        RefreshTiles(history);
    }

    public void RefreshTiles(List<WatchEntry>? history = null)
    {
        history ??= host.History.ToList();
        EpisodeTiles.Clear();
        foreach (var e in Episodes)
        {
            var h = history.FirstOrDefault(h => h.AnimeId == host.SelectedAnime?.ShikimoriId && h.ProviderId == SelectedProvider?.Id && h.ReleaseId == SelectedRelease?.Id && h.EpisodeId == e.Id);
            var watched = h?.Completed == true || (e.CountsTowardProgress && e.Number > 0 && e.Number <= host.Watched);
            var partial = !watched && h?.Position > 0;
            var number = e.Number > 0 ? e.Number.ToString() : e.Title;
            if (Episodes.Count(other => other.Number == e.Number) > 1) number = e.Title;
            var downloaded = DownloadLibrary.Find(savedEpisodes, SelectedProvider?.Id ?? "", SelectedRelease?.Id ?? "", e.Id) is not null;
            var suffix = (downloaded ? " ↓" : "") + (watched ? " ✓" : "");
            var resumeText = partial ? "С " + TimeSpan.FromSeconds(h!.Position).ToString(h.Position >= 3600 ? @"h\:mm\:ss" : @"mm\:ss") : "";
            EpisodeTiles.Add(new(e, number + suffix, e.Title + (partial ? $" · продолжить с {h!.Position:0} сек." : ""), watched, e.Id == SelectedEpisode?.Id, partial, resumeText));
        }
    }

    [RelayCommand]
    private void SelectEpisode(ProviderEpisode? episode)
    {
        SelectedEpisode = episode;
        RefreshTiles();
    }

    [RelayCommand]
    private Task Play() => host.Run(async () =>
    {
        var requestedRelease = SelectedRelease;
        await host.CardReady;
        if (requestedRelease != SelectedRelease) return;
        if (host.SelectedAnime is null || SelectedProvider is null || SelectedRelease is null || SelectedEpisode is null)
            throw new InvalidOperationException("Выберите релиз и серию.");
        if (host.SelectedAnime.ShikimoriId > 0 && !(await repository.Library(host.UserId)).Any(a => a.ShikimoriId == host.SelectedAnime.ShikimoriId))
            await repository.Cache(host.UserId, [host.SelectedAnime]);
        var source = await DownloadLibrary.Resolve(repository, SelectedProvider.Provider, SelectedRelease.Id, SelectedEpisode);
        var last = (await repository.History(host.UserId)).FirstOrDefault(h => h.AnimeId == host.SelectedAnime.ShikimoriId && h.ProviderId == SelectedProvider.Id && h.ReleaseId == SelectedRelease.Id && h.EpisodeId == SelectedEpisode.Id && !h.Completed);
        await playback.Start(new(host.UserId, host.SelectedAnime.ShikimoriId, host.SelectedAnime.Title, SelectedProvider.Id, SelectedRelease.Id, SelectedEpisode.Id, SelectedEpisode.Number, source, last?.Position ?? 0, host.SelectedAnime.ShikimoriId > 0 && SelectedEpisode.CountsTowardProgress));
        host.Message = "Просмотр открыт в AnimeApp";
        await host.Reload();
    });
    [RelayCommand]
    private Task OpenRelease() => host.Run(async () =>
    {
        if (SelectedRelease is null || string.IsNullOrEmpty(SelectedRelease.Url))
            throw new InvalidOperationException("У релиза нет веб-страницы.");
        await new SystemPlaybackLauncher().Open(new(host.UserId, host.SelectedAnime?.ShikimoriId ?? 0, "", SelectedProvider?.Id ?? "", SelectedRelease.Id, "", 0, new(PlaybackKind.WebPage, SelectedRelease.Url)));
    });
}

