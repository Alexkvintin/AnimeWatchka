using System.Security.Cryptography;
using System.Text;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Providers;

namespace AnimeApp.Services.Torrents;

public sealed partial class TorrentProvider
{
    public static double StreamingScore(TorrentSearchResult result) {
        var range=System.Text.RegularExpressions.Regex.Match(result.Episodes,@"^(\d+)-(\d+)$");
        var count=range.Success ? Math.Max(1,int.Parse(range.Groups[2].Value)-int.Parse(range.Groups[1].Value)+1) : 1;
        var gib=Math.Max(0.15,result.Size/1073741824d/count);
        return Math.Log2(result.Seeds+1)/Math.Sqrt(gib);
    }
    private readonly SemaphoreSlim playbackGate = new(1);

    public static ProviderEpisode[] CatalogEpisodes(string id, IEnumerable<TorrentSearchResult> results, int episodeLimit=0) =>
        Enumerable.Range(1,episodeLimit>0 ? Math.Min(episodeLimit,999) : 999).Where(n => results.Any(r => TorrentCatalogFormats.CoversEpisode(r,n)))
            .Select(n => new ProviderEpisode("episode:"+n,n,$"Серия {n}",id)).ToArray();

    public async Task<ProviderRelease> SaveCatalog(string identity,string title,IReadOnlyList<TorrentSearchResult> results,CancellationToken ct,int episodeLimit=0,IReadOnlyList<string>? searchNames=null)
    {
        // Old season packs often have no episode range in the catalog title.
        // Read their small .torrent metadata without starting a download session.
        var expanded=results.ToList();
        using var metadataBudget=CancellationTokenSource.CreateLinkedTokenSource(ct);
        metadataBudget.CancelAfter(TimeSpan.FromSeconds(5));
        var batches=await Task.WhenAll(results.Where(r=>!Enumerable.Range(1,999).Any(n=>TorrentCatalogFormats.CoversEpisode(r,n)) && !string.IsNullOrEmpty(r.TorrentUrl))
            .OrderByDescending(r=>r.Seeds).Take(3).Select(async r=> {
                try {
                    var imported=await ImportResult(r,metadataBudget.Token);
                    var path=await repository.Setting<string>("torrent-input-"+imported.Id,metadataBudget.Token);
                    var metadata=await MonoTorrent.Torrent.LoadAsync(path!);
                    return metadata.Files.Where(f=>IsVideo(f.Path) && EpisodeNumber(f.Path)>0)
                        .Select(f=>r with {Episodes=EpisodeNumber(f.Path).ToString(System.Globalization.CultureInfo.InvariantCulture), Size=f.Length}).ToArray();
                } catch(Exception ex) when(ex is IOException or HttpRequestException or OperationCanceledException) { ct.ThrowIfCancellationRequested(); return Array.Empty<TorrentSearchResult>(); }
            }));
        expanded.AddRange(batches.SelectMany(b=>b));
        results=expanded;
        var id="catalog-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        await repository.SaveSetting("torrent-catalog-"+id,results.ToArray(),ct);
        await repository.SaveSetting("torrent-catalog-names-"+id,searchNames ?? new[]{title},ct);
        await repository.SaveSetting("torrent-title-"+id,title,ct);
        await repository.SaveSetting("torrent-episodes-"+id,CatalogEpisodes(id,results,episodeLimit),ct);
        return new(id,title);
    }

    private async Task<PlaybackSource> ResolveCatalogEpisode(string id,ProviderEpisode episode,CancellationToken ct,bool refresh=true)
    {
        if(episode.ReleaseId!=id || episode.Id!="episode:"+episode.Number) throw new IOException("Выберите серию заново.");
        if(!(await GetSavedEpisodes(id,ct)).Any(e=>e.Id==episode.Id && e.Number==episode.Number)) throw new IOException("Список серий обновился. Откройте карточку заново.");
        var results=await repository.Setting<TorrentSearchResult[]>("torrent-catalog-"+id,ct) ?? [];
        var candidates=results.Where(r=>TorrentCatalogFormats.CoversEpisode(r,episode.Number))
            .OrderByDescending(r=>r.Seeds>0).ThenByDescending(StreamingScore).Take(4).ToArray();
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(35));
        foreach(var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if(budget.IsCancellationRequested) break;
            try
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                var release=await ImportResult(candidate,timeout.Token);
                var manager=await Open(release.Id,timeout.Token);
                var files=manager.Files.Where(f=>IsVideo(f.Path) && EpisodeNumber(f.Path)==episode.Number).ToArray();
                // Never substitute another episode when a catalog label is wrong.
                if(files.Length!=1) continue;
                return await session.Play(files[0].Path,timeout.Token);
            }
            catch(Exception ex) when(ex is IOException or HttpRequestException or OperationCanceledException)
            { ct.ThrowIfCancellationRequested(); }
        }
        if(refresh)
        {
            var names=await repository.Setting<string[]>("torrent-catalog-names-"+id,ct) ?? [];
            var queries=names.Take(2).Select(n=>n+" - "+episode.Number.ToString("00")).Concat(names.Take(2));
            var fresh=await FindRussian(queries,ct,includeOriginalAudio:true);
            var attempted=candidates.Select(r=>r.Magnet).ToHashSet();
            var alternatives=fresh.Where(r=>!attempted.Contains(r.Magnet) && TorrentCatalogFormats.CoversEpisode(r,episode.Number)).ToArray();
            if(alternatives.Length>0) {
                await repository.SaveSetting("torrent-catalog-"+id,alternatives.Concat(results.Where(r=>!attempted.Contains(r.Magnet))).DistinctBy(r=>(r.Magnet,r.Episodes)).ToArray(),ct);
                return await ResolveCatalogEpisode(id,episode,ct,false);
            }
        }
        throw new IOException("Сейчас серия недоступна: проверены доступные варианты. Нажмите «Смотреть выбранную серию», чтобы повторить автоматический поиск позже.");
    }
}
