using AnimeApp.Models;

namespace AnimeApp.Services.Providers;
public record AnimeIdentity(string Provider, string Id);
public record AnimeRelation(Anime Anime, string Relation)
{
    public bool IsSeason => (Relation is "prequel" or "sequel") && (Anime.Kind is "tv" or "ona");
    public string RelationLabel => Relation switch
    {
        "side_story" => "Побочная история",
        "spin_off" => "Ответвление",
        "alternative_setting" => "Другой мир",
        "alternative_version" => "Другая версия",
        "summary" => "Краткий пересказ",
        "parent_story" => "Основная история",
        "full_story" => "Полная история",
        "character" => "Общие персонажи",
        "other" => "Связанное",
        _ => Relation
    };
    public string Label => $"{(Relation == "prequel" ? "← Предыдущий" : Relation == "sequel" ? "Следующий →" : RelationLabel)} · {Anime.Kind.ToUpperInvariant()} · {Anime.Title}";
}

public interface IMetadataProvider
{
    string MetadataId { get; }

    string MetadataName { get; }

    Task<Anime> Details(AnimeIdentity identity, CancellationToken ct = default) => identity.Provider == MetadataId && long.TryParse(identity.Id, out var id) ? Details(id, ct) : throw new ArgumentException("Тайтл принадлежит другому источнику метаданных.");
    Task<IReadOnlyList<AnimeRelation>> Relations(AnimeIdentity identity, CancellationToken ct = default) => identity.Provider == MetadataId && long.TryParse(identity.Id, out var id) ? Relations(id, ct) : throw new ArgumentException("Тайтл принадлежит другому источнику метаданных.");
    Task<List<Anime>> Search(string query, CancellationToken ct = default);
    Task<IReadOnlyList<Anime>> FilterContent(IEnumerable<Anime> anime, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Anime>>(anime.ToArray());
    Task<IReadOnlyDictionary<long, string>> Posters(IEnumerable<long> ids, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<long, string>>(new Dictionary<long, string>());
    bool SupportsSearchFilters => false;

    async Task<List<Anime>> Search(AnimeSearch query, CancellationToken ct = default) => (await Search(query.Query, ct)).Where(query.Matches).ToList();
    Task<Anime> Details(long id, CancellationToken ct = default);
    Task<IReadOnlyList<AnimeRelation>> Relations(long id, CancellationToken ct = default);
}

public record MetadataChoice(string Id, string Name, bool Supported)
{
    public override string ToString() => Name + (Supported ? "" : " · позже");
}

public record ScheduleItem(Anime Anime, int ExpectedEpisode, DateTimeOffset? AirDateTime)
{
    public AnimeIdentity Identity => Anime.Identity;
    public string ProviderId { get; init; } = "";
    public string ProviderName { get; init; } = "";
    public ProviderRelease? Release { get; init; }
    public DayOfWeek? ProviderDay { get; init; }
    public DayOfWeek? DayOfWeek => ProviderDay ?? AirDateTime?.ToLocalTime().DayOfWeek;
    public bool Released { get; init; }
    public string TimeHint { get; init; } = "";
    public string Poster => !string.IsNullOrWhiteSpace(Release?.Poster) ? Release.Poster : Anime.Poster;
    public string PosterFallback => Anime.Poster;
    public string GroupSources { get; init; } = "";
    public string SourcesLabel => GroupSources == "" ? ProviderName : GroupSources;
    public bool CanOpen => Anime.ShikimoriId > 0 || Release is not null;
    public string ActionLabel => Released ? "Смотреть" : "Открыть";
    public string Expected => ProviderId == "" ? $"Общее расписание: серия {ExpectedEpisode} · {AirDateTime?.ToLocalTime():dd.MM HH:mm}" : (ExpectedEpisode > 0 ? $"Серия {ExpectedEpisode} · " : "") + TimeHint;
    public string Availability { get; init; } = "Доступность серии ещё не проверена";
}

public interface IScheduleProvider
{
    Task<IReadOnlyList<ScheduleItem>> Schedule(CancellationToken ct = default);
}

public interface IUserLibraryProvider
{
    Task<UserIdentity> CurrentUser(CancellationToken ct = default);
    Task<List<Anime>> Rates(long user, CancellationToken ct = default);
    Task Update(long user, Anime anime, CancellationToken ct = default);
    async Task<Anime?> ReadRate(long user, Anime anime, CancellationToken ct = default) => (await Rates(user, ct)).FirstOrDefault(a => a.Identity == anime.Identity);
    Task UpdateFields(long user, Anime anime, PendingChange change, CancellationToken ct = default) => change.Score is not null ? throw new NotSupportedException("Источник не поддерживает оценки.") : Update(user, anime with { EpisodesWatched = change.EpisodesChanged ? change.Episodes : anime.EpisodesWatched, UserStatus = change.StatusChanged ? change.Status : anime.UserStatus }, ct);
}
