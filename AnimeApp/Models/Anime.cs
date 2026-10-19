namespace AnimeApp.Models;
public record Anime
{
    public string MetadataProviderId { get; init; } = "shikimori";
    public AnimeApp.Services.Providers.AnimeIdentity Identity => new(MetadataProviderId, ShikimoriId.ToString());
    public long ShikimoriId { get; init; }
    public string Name { get; init; } = "";
    public string RussianName { get; init; } = "";
    public string EnglishName { get; init; } = "";
    public string JapaneseName { get; init; } = "";
    public IReadOnlyList<string> AlternativeNames { get; init; } = [];
    public string Poster { get; init; } = "";
    public string Description { get; init; } = "Описание пока не загружено.";
    public int? Year { get; init; }
    public int? AirMonth { get; init; }
    public string Status { get; init; } = "";
    public string Kind { get; init; } = "";
    public int EpisodesTotal { get; init; }
    public int EpisodesWatched { get; init; }
    public string UserStatus { get; init; } = "planned";
    public int Score { get; init; }
    public double CommunityScore { get; init; }
    public string RatingLabel => CommunityScore > 0 ? $"★ {CommunityScore:0.00} · Shikimori" : "";
    public long? RateId { get; init; }
    public DateTimeOffset? AddedAt { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public AnimeApp.Services.ContinuePlan? Continuation { get; init; }
    public bool HasContinuation => Continuation?.Available == true && !string.IsNullOrWhiteSpace(Continuation.Label);
    public string Title => string.IsNullOrWhiteSpace(RussianName) ? Name : RussianName;
    public string Progress => $"{EpisodesWatched} / {(EpisodesTotal == 0 ? "?" : EpisodesTotal)} серий";
    public int Rewatches { get; init; }
    public string Metadata => $"{Year?.ToString() ?? "—"}  •  {Kind.ToUpperInvariant()}  •  {StatusLabels.Get(Status)}";
}

public record AnimeSourceMapping(long ShikimoriId, string ProviderId, string ProviderAnimeId);
public record UnlinkedProgress(PendingChange Change, string Title)
{
    public string Description => $"{Title}: {Change.Episodes} серий. Сохранено на устройстве; нет связи с тайтлом Shikimori.";
}
public record PendingChange(long UserId, long AnimeId, string Revision, int Episodes, string Status, int BaseEpisodes, string BaseStatus, bool ExistedRemotely = false, bool EpisodesChanged = true, bool StatusChanged = true, int? Score = null, int? BaseScore = null);
public record WatchEntry(long AnimeId, string Title, string ProviderId, string ReleaseId, string EpisodeId, int Episode, double Position, DateTimeOffset UpdatedAt, double Duration = 0, bool Completed = false, IReadOnlyList<WatchRange>? WatchedRanges = null);
public record AppSettings(bool AutoMark = true, int Threshold = 90, bool AutoSync = true, bool SavePosition = true, string Quality = "Auto", string LocalFolder = "", bool AutoResume = true);
public record ProviderSettings(string Id, bool Enabled = true, int Priority = 0);
public record UserIdentity(long Id, string Nickname, string Avatar = "");
public record WatchRange(double Start, double End);
