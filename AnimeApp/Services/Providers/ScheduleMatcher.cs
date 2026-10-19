using AnimeApp.Models;
using AnimeApp.Services.Database;

namespace AnimeApp.Services.Providers;
public sealed class ScheduleMatcher(IMetadataProvider metadata, LibraryRepository repository)
{
    public async Task<ScheduleItem> Enrich(ScheduleItem item, IReadOnlyList<Anime> library, CancellationToken ct)
    {
        if (item.Release is null)
            return item;
        var key = "schedule-map:" + item.ProviderId + ":" + item.Release.Id;
        var saved = await repository.Setting<Anime>(key + ":anime", ct);
        if (saved is { ShikimoriId: > 0 })
            return item with
            {
                Anime = library.FirstOrDefault(a => a.Identity == saved.Identity) ?? saved,
                Release = item.Release with
                {
                    MetadataIdentity = saved.Identity
                }
            };
        var identity = await repository.MappedIdentity(item.ProviderId, item.Release.Id, ct) ?? item.Release.MetadataIdentity ?? (item.Anime.ShikimoriId > 0 ? item.Identity : await repository.Setting<AnimeIdentity>(key, ct));
        if (identity is not null && (!long.TryParse(identity.Id, out var validId) || validId <= 0)) identity = null;
        Anime? matched = identity is null ? null : library.FirstOrDefault(a => a.Identity == identity);
        if (identity is null)
        {
            var matcher = new ReleaseMatcher();
            var candidates = library.Where(a => a.ShikimoriId > 0).Select(a => (Anime: a, Score: matcher.Rank(a, item.Release).Score)).OrderByDescending(x => x.Score).ToArray();
            if (candidates.Length > 0 && candidates[0].Score >= .9 && (candidates.Length == 1 || candidates[1].Score < .65))
                matched = candidates[0].Anime;
            else
            {
                var found = new List<Anime>();
                foreach (var query in item.Release.Title.Split('/').Select(s => s.Trim()).Where(s => s != "").Distinct().Take(2))
                    found.AddRange(await metadata.Search(new AnimeSearch(query, item.Release.Year, item.Release.Year, Kind: item.Release.Kind), ct));
                found = found.Where(a => a.ShikimoriId > 0).DistinctBy(a => a.Identity).ToList();
                var ranked = found.Select(a => (Anime: a, Score: matcher.Rank(a, item.Release).Score)).OrderByDescending(x => x.Score).ToArray();
                if (ranked.Length > 0 && ranked[0].Score >= .9 && (ranked.Length == 1 || ranked[1].Score < .65))
                    matched = ranked[0].Anime;
            }

            identity = matched?.Identity;
            if (identity is not null)
                await repository.SaveSetting(key, identity, ct);
        }

        if (identity is not null && matched is null)
            matched = await metadata.Details(identity, ct);
        if (matched is not null)
            await repository.SaveSetting(key + ":anime", matched, ct);
        return matched is null ? item : item with
        {
            Anime = matched,
            Release = item.Release with
            {
                MetadataIdentity = matched.Identity
            }
        };
    }
}
