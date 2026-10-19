using System.Collections.ObjectModel;
using AnimeApp.Models;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Database;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeApp.ViewModels;
public sealed record ScheduleDay(DayOfWeek Day, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class ScheduleViewModel(IScheduleProvider provider, LibraryRepository repository, SourcesViewModel sources) : ObservableObject
{
    public static ScheduleDay[] Week { get; } = [new(DayOfWeek.Monday, "Понедельник"), new(DayOfWeek.Tuesday, "Вторник"), new(DayOfWeek.Wednesday, "Среда"), new(DayOfWeek.Thursday, "Четверг"), new(DayOfWeek.Friday, "Пятница"), new(DayOfWeek.Saturday, "Суббота"), new(DayOfWeek.Sunday, "Воскресенье")];
    public ScheduleDay[] Days => Week;
    public string[] Modes { get; } = ["Мой источник"];
    public string[] Statuses { get; } = ["Все", "Уже вышли", "Ожидаются"];
    public ObservableCollection<FilterChoice> ProviderChoices { get; } = [];
    public ObservableCollection<ScheduleItem> Items { get; } = [];

    [ObservableProperty]
    private string mode = "Мой источник";
    [ObservableProperty]
    private string statusFilter = "Все";
    [ObservableProperty]
    private bool onlyLibrary;
    [ObservableProperty]
    private FilterChoice? selectedSource;
    [ObservableProperty]
    private ScheduleDay selectedDay = Week.First(d => d.Day == DateTime.Now.DayOfWeek);
    [ObservableProperty]
    private string message = "Расписание ещё не загружено";
    public bool IsSourceMode => Mode != "Общее";

    private CancellationTokenSource? loading;
    private bool refreshingChoices;
    private readonly Dictionary<string, (DateTimeOffset At, ScheduleItem Item)> enriched = new();
    private readonly HashSet<string> updating = new();
    partial void OnModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsSourceMode));
        _ = Refresh();
    }

    partial void OnSelectedSourceChanged(FilterChoice? value)
    {
        if (!refreshingChoices)
            _ = Refresh();
    }

    partial void OnSelectedDayChanged(ScheduleDay value) => _ = Refresh();
    partial void OnStatusFilterChanged(string value) => _ = Refresh();
    partial void OnOnlyLibraryChanged(bool value) => _ = Refresh();
    public static IEnumerable<ScheduleItem> ForDay(IEnumerable<ScheduleItem> items, DayOfWeek day) => items.Where(i => i.DayOfWeek == day).OrderBy(i => i.AirDateTime);
    public void RefreshChoices()
    {
        refreshingChoices = true;
        var selected = SelectedSource?.Id ?? sources.PreferredProviderId;
        var choices = sources.Providers.Where(p => p.Enabled && p.Provider is IScheduleProvider && p.Provider.Features.HasFlag(ProviderCapabilities.Schedule)).OrderBy(p => p.Priority)
            .Select(p => new FilterChoice(p.Id, p.Name)).Append(new("all", "Все источники")).ToArray();
        // Preserve the ComboBox's selected instance during day/status refreshes.
        if (!ProviderChoices.SequenceEqual(choices))
        {
            SelectedSource = null;
            ProviderChoices.Clear();
            foreach (var choice in choices) ProviderChoices.Add(choice);
        }
        SelectedSource = ProviderChoices.FirstOrDefault(p => p.Id == selected) ?? ProviderChoices.FirstOrDefault();
        refreshingChoices = false;
    }

    public static IReadOnlyList<ScheduleItem> Group(IEnumerable<ScheduleItem> items) => items.GroupBy(i => i.Anime.ShikimoriId > 0 ? "meta:" + i.Identity.Provider + ":" + i.Identity.Id : "source:" + i.ProviderId + ":" + i.Release?.Id).Select(g => g.OrderByDescending(i => i.Released).First()with { GroupSources = string.Join(" · ", g.Select(i => i.ProviderName + (i.Released ? " ✓" : " — ожидается")).Distinct()) }).ToArray();
    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task Refresh()
    {
        loading?.Cancel();
        loading = new();
        var ct = loading.Token;
        RefreshChoices();
        Items.Clear();
        Message = "Загружаем расписание…";
        var general = !IsSourceMode;
        var source = SelectedSource?.Id;
        var day = SelectedDay.Day;
        var filter = StatusFilter;
        var only = OnlyLibrary;
        try
        {
            var library = await repository.Library(sources.UserId, ct);
            var all = new List<ScheduleItem>();
            var unavailable = new List<string>();
            if (general)
                all.AddRange(await Cached("general", () => provider.Schedule(ct), ct));
            else
                foreach (var p in sources.Providers.Where(p => p.Enabled && p.Provider is IScheduleProvider && (source == "all" || source == p.Id)))
                {
                    try
                    {
                        var result = await Cached(p.Id, () => ((IScheduleProvider)p.Provider).Schedule(ct), ct);
                        // A metadata calendar item is never accepted as a studio release.
                        all.AddRange(result.Where(i => i.ProviderId == p.Id && !string.IsNullOrWhiteSpace(i.Release?.Id)));
                    }
                    catch (IOException)when (source == "all")
                    {
                        unavailable.Add(p.Name);
                    }
                }

            var visible = new List<ScheduleItem>();
            // Show provider rows immediately; metadata enrichment must not blank the calendar.
            if (!only && !general)
            {
                foreach (var initial in ForDay(all, day).Where(i => filter == "Все" || (filter == "Уже вышли" ? i.Released : !i.Released)))
                    Items.Add(initial);
                Message = $"Показано: {Items.Count}.";
            }

            foreach (var original in ForDay(all, day))
            {
                ct.ThrowIfCancellationRequested();
                var item = original;
                if (!general && provider is IMetadataProvider metadata)
                {
                    var key = item.ProviderId + ":" + item.Release!.Id;
                    if (enriched.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(3))
                        item = item with
                        {
                            Anime = cached.Item.Anime,
                            Release = item.Release with
                            {
                                MetadataIdentity = cached.Item.Release?.MetadataIdentity
                            }
                        };
                    else
                        try
                        {
                            item = await new ScheduleMatcher(metadata, repository).Enrich(item, library, ct);
                            enriched[key] = (DateTimeOffset.UtcNow, item);
                        }
                        catch (IOException)
                        {
                        }
                }

                if (general)
                {
                    foreach (var p in sources.EnabledProviders)
                    {
                        var mapping = await repository.Mapping(item.Anime.ShikimoriId, p.Id, ct);
                        if (mapping is null)
                            continue;
                        try
                        {
                            var episodes = await p.Provider.GetEpisodes(mapping, ct);
                            var ready = episodes.Any(e => e.Available && e.Number == item.ExpectedEpisode);
                            item = item with
                            {
                                Released = ready,
                                Availability = ready ? $"{p.Name}: ✓ серия {item.ExpectedEpisode} доступна" : $"{p.Name}: ожидается серия {item.ExpectedEpisode}"};
                        }
                        catch (IOException)
                        {
                            item = item with
                            {
                                Availability = "Источник временно недоступен"
                            };
                        }

                        if (item.Released)
                            break;
                    }
                }

                if (only && !library.Any(a => a.Identity == item.Identity))
                    continue;
                if (filter == "Уже вышли" && !item.Released || filter == "Ожидаются" && item.Released)
                    continue;
                visible.Add(item);
            }

            ct.ThrowIfCancellationRequested();
            Items.Clear();
            foreach (var item in source == "all" && !general ? Group(visible) : visible)
                Items.Add(item);
            Message = (general ? "Общее расписание · календарь Shikimori, не расписание озвучки." : "Только подтверждённые релизы выбранного источника. ") + $" Показано: {Items.Count}.";
            if (unavailable.Count > 0)
                Message += " Временно недоступны: " + string.Join(", ", unavailable) + ".";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Message = "Не удалось загрузить расписание: " + (ex is IOException ? ex.Message : "попробуйте позже");
        }
    }

    async Task<IReadOnlyList<ScheduleItem>> Cached(string id, Func<Task<IReadOnlyList<ScheduleItem>>> fetch, CancellationToken ct)
    {
        var saved = await repository.Setting<List<ScheduleItem>>("schedule-v2:" + id, ct);
        var at = await repository.Setting<DateTimeOffset>("schedule-at:" + id, ct);
        if (saved is not null)
        {
            if (DateTimeOffset.UtcNow - at > TimeSpan.FromMinutes(15) && updating.Add(id))
                _ = UpdateCache(id, fetch, ct);
            return saved;
        }

        try
        {
            var data = await fetch();
            await repository.SaveSetting("schedule-v2:" + id, data, ct);
            await repository.SaveSetting("schedule-at:" + id, DateTimeOffset.UtcNow, ct);
            return data;
        }
        catch (IOException)
        {
            var data = await repository.Setting<List<ScheduleItem>>("schedule-v2:" + id, ct);
            if (data is null)
                throw;
            return data.Select(i => i with { Availability = i.Availability + " · сохранённые данные" }).ToArray();
        }
    }

    private async Task UpdateCache(string id, Func<Task<IReadOnlyList<ScheduleItem>>> fetch, CancellationToken ct)
    {
        try
        {
            var data = await fetch();
            await repository.SaveSetting("schedule-v2:" + id, data, ct);
            await repository.SaveSetting("schedule-at:" + id, DateTimeOffset.UtcNow, ct);
            if (!ct.IsCancellationRequested)
                await Refresh();
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            updating.Remove(id);
        }
    }

    [RelayCommand]
    private async Task ForceRefresh()
    {
        var id = IsSourceMode ? SelectedSource?.Id : "general";
        if (id == "all")
            foreach (var provider in ProviderChoices.Where(p => p.Id != "all"))
                await repository.SaveSetting("schedule-at:" + provider.Id, DateTimeOffset.MinValue);
        else if (id is not null)
            await repository.SaveSetting("schedule-at:" + id, DateTimeOffset.MinValue);
        await Refresh();
    }
}

