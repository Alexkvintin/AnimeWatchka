namespace AnimeApp.Services.Providers;
public record ProviderRelease(string Id, string Title, string Url = "", int? Year = null, string Kind = "", int? EpisodesTotal = null, int? EpisodesAvailable = null, string Poster = "", AnimeIdentity? MetadataIdentity = null)
{
    public override string ToString() => Title;
}

public record ProviderEpisode(string Id, int Number, string Title, string ReleaseId = "", string PageUrl = "", bool Available = true, bool CountsTowardProgress = true)
{
    public override string ToString() => $"{Number:00}   {Title}";
}

public record ProviderAvailability(bool Available, string Message);
public interface IAnimeProvider : IPlaybackProvider
{
}

public interface IPlaybackProvider
{
    string Id { get; }

    string DisplayName { get; }

    string Language { get; }

    string Url => "";

    ProviderCapabilities Features => ProviderCapabilities.Search | ProviderCapabilities.Episodes | ProviderCapabilities.DirectPlayback;

    string Capabilities => CapabilityLabels.Describe(Features);

    Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default);
    Task<IReadOnlyList<ProviderRelease>> Search(string query, CancellationToken ct = default);
    Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default);
    Task<Playback.PlaybackSource> GetPlayback(string releaseId, ProviderEpisode episode, CancellationToken ct = default);
}

public sealed class ProviderRegistry(IEnumerable<IAnimeProvider> providers)
{
    private readonly List<IAnimeProvider> providersList = providers.ToList();
    public IReadOnlyList<IAnimeProvider> All => providersList;

    public void Register(IAnimeProvider provider)
    {
        if (!providersList.Any(p => p.Id == provider.Id))
            providersList.Add(provider);
    }

    public IAnimeProvider Get(string id) => All.Single(p => p.Id == id);
}

public sealed class ProviderException(string message, Exception? inner = null) : IOException(message, inner);
