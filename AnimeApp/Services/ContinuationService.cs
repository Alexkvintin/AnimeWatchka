using AnimeApp.Models;
using AnimeApp.Services.Database;
using AnimeApp.Services.Providers;

namespace AnimeApp.Services;
public sealed record ContinuePlan(string ProviderId, string SourceName, string ReleaseId, ProviderEpisode? Episode, double Position, string Hint)
{
    public bool Available => Episode is not null;
    public string Label => Episode is null ? "Открыть карточку" : Position > 0 ? $"Серия {Episode.Number} · с {TimeLabels.Format(Position)}" : $"Смотреть серию {Episode.Number}";
}

public static class TimeLabels
{
    public static string Format(double seconds) => TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
}

public sealed class ContinuationService(LibraryRepository repository)
{
    public async Task<ContinuePlan> Find(Anime anime, IReadOnlyList<WatchEntry> history, IReadOnlyList<IAnimeProvider> providers, CancellationToken ct = default)
    {
        var recent = history.FirstOrDefault(h => h.AnimeId == anime.ShikimoriId);
        foreach (var provider in providers.OrderByDescending(p => p.Id == recent?.ProviderId))
        {
            var release = await repository.Mapping(anime.ShikimoriId, provider.Id, ct);
            if (release is null)
                continue;
            try
            {
                var episodes = await provider.GetEpisodes(release, ct);
                var resume = history.FirstOrDefault(h => h.AnimeId == anime.ShikimoriId && h.ProviderId == provider.Id && h.ReleaseId == release && !h.Completed && h.Position > 0 && episodes.Any(e => e.Id == h.EpisodeId && e.Available));
                var episode = resume is not null ? episodes.First(e => e.Id == resume.EpisodeId) : episodes.Where(e => e.Available && e.CountsTowardProgress && e.Number > anime.EpisodesWatched).OrderBy(e => e.Number).FirstOrDefault();
                return new(provider.Id, provider.DisplayName, release, episode, resume?.Position ?? 0, episode is null ? "Новых серий в источнике пока нет" : resume is not null ? "Есть сохранённая позиция" : "Следующая доступная серия");
            }
            catch (IOException)
            {
                return new(provider.Id, provider.DisplayName, release, null, 0, "Источник недоступен. Откройте карточку для повторной проверки.");
            }
            catch (InvalidOperationException)
            {
                return new(provider.Id, provider.DisplayName, release, null, 0, "Сохранённый релиз недоступен — выберите другой в карточке.");
            }
        }

        return new("", "Источник не выбран", "", null, 0, "Открыть список серий");
    }
}

public sealed record HistoryItem(WatchEntry Entry, string Poster, string SourceName)
{
    public string Title => Entry.Title;
    public string Episode => $"Серия {Entry.Episode}";
    public string Date => Entry.UpdatedAt.ToLocalTime().ToString("dd.MM.yyyy · HH:mm");
    public string Progress => $"{TimeLabels.Format(Entry.Position)} / {(Entry.Duration > 0 ? TimeLabels.Format(Entry.Duration) : "—")}";
    public string State => Entry.Completed ? "Просмотрено" : "Просмотр не завершён";
    public string Action => Entry.Completed ? "Смотреть снова →" : "Продолжить →";
}
