using AnimeApp.Services.Providers;

namespace AnimeApp.ViewModels;
public sealed partial class SourcesViewModel
{
    private readonly Dictionary<(string Provider, string Release), (DateTimeOffset At, ProviderRelease Value)> releaseCache = new();
    private readonly Dictionary<(string Provider, string Release), (DateTimeOffset At, IReadOnlyList<ProviderEpisode> Value)> episodeCache = new();
    private async Task<ProviderRelease> CachedRelease(IAnimeProvider provider, string id, CancellationToken ct = default)
    {
        if (releaseCache.TryGetValue((provider.Id, id), out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(3)) return cached.Value;
        var value = await provider.GetRelease(id, ct);
        ct.ThrowIfCancellationRequested();
        if (releaseCache.Count > 100) releaseCache.Clear();
        releaseCache[(provider.Id, id)] = (DateTimeOffset.UtcNow, value);
        return value;
    }
    private async Task<IReadOnlyList<ProviderEpisode>> CachedEpisodes(IAnimeProvider provider, string id, CancellationToken ct = default)
    {
        if (episodeCache.TryGetValue((provider.Id, id), out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(3)) return cached.Value;
        var saved = provider is IOfflineEpisodeCatalog offline ? await offline.GetSavedEpisodes(id, ct) : [];
        var value = saved.Count > 0 ? saved : await provider.GetEpisodes(id, ct);
        ct.ThrowIfCancellationRequested();
        if (episodeCache.Count > 100) episodeCache.Clear();
        if (value.Count > 0) episodeCache[(provider.Id, id)] = (DateTimeOffset.UtcNow, value);
        return value;
    }
}
