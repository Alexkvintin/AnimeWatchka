using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using AnimeApp.Services.Database;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Providers;

namespace AnimeApp.Services.Torrents;
public record TorrentSourceManifest(int Version, List<TorrentSearchSource> Sources);
public record TorrentSearchSource(string Name, string SearchUrl, bool Enabled = true, string Format = "json");
public record TorrentSearchResult(string Title, string Magnet, string Quality = "", long Size = 0, int Seeds = 0, string Episodes = "", string[]? SubtitleLanguages = null, bool JapaneseAudioCandidate = false, string TorrentUrl = "", string InfoHash = "")
{
    public override string ToString() => $"{(JapaneseAudioCandidate ? "Перевод подбирается отдельно · " : "")}{Title} · {Quality} · {Size / 1073741824d:0.00} ГБ · сидов: {Seeds} · серии: {Episodes}";
}
public sealed partial class TorrentProvider(LibraryRepository repository, TorrentSession session, HttpMessageHandler? searchHandler = null) : IAnimeProvider, IOfflineEpisodeCatalog
{
    private readonly SemaphoreSlim searchSlots = new(4);
    private readonly HttpClient searchHttp = searchHandler is null ? http : new HttpClient(searchHandler) { Timeout=TimeSpan.FromSeconds(8) };
    public bool Playing { get; set; }
    public bool Downloading { get; set; }
    public string Status => session.Status;
    public string Id => "torrent-subtitles";
    public string DisplayName => "Субтитры";
    public string Language => "ja";
    public string Url => "";
    public ProviderCapabilities Features => ProviderCapabilities.Episodes | ProviderCapabilities.DirectPlayback;
    private static readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    public Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default) => Task.FromResult(new ProviderAvailability(true,"Добавьте свой источник поиска или magnet/.torrent."));
    public Task<IReadOnlyList<ProviderRelease>> Search(string query,CancellationToken ct=default) => Task.FromResult<IReadOnlyList<ProviderRelease>>([]);
    public static void ValidateSource(TorrentSearchSource source) {
        if(source is null || source.Format is not ("json" or "nyaa" or "animetosho") || string.IsNullOrWhiteSpace(source.SearchUrl) || string.IsNullOrWhiteSpace(source.Name) || source.Name.Length>100 || source.SearchUrl.Length>2048 || !source.SearchUrl.Contains("{query}") || !Uri.TryCreate(source.SearchUrl.Replace("{query}","test"),UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.UserInfo!="" || uri.IsLoopback)
            throw new IOException("Нужен HTTPS-адрес поиска с {query}. JavaScript-расширения не поддерживаются.");
    }
    public static IReadOnlyList<TorrentSearchSource> ParseManifest(string json) {
        if (json.Length > 256*1024) throw new IOException("Слишком большой манифест.");
        var manifest=JsonSerializer.Deserialize<TorrentSourceManifest>(json,new JsonSerializerOptions { PropertyNameCaseInsensitive=true });
        if(manifest?.Version!=1 || manifest.Sources is null || manifest.Sources.Count>20) throw new IOException("Нужен манифест AnimeWatchka версии 1, до 20 источников. JavaScript Hayase не поддерживается.");
        foreach(var source in manifest.Sources) ValidateSource(source);
        return manifest.Sources.Select(s=>s with { Enabled=false }).ToArray();
    }
    public static async Task<IReadOnlyList<TorrentSearchSource>> ReadManifest(string location,CancellationToken ct=default) {
        if(File.Exists(location)) {
            if(new FileInfo(location).Length>256*1024) throw new IOException("Слишком большой манифест.");
            return ParseManifest(await File.ReadAllTextAsync(location,ct));
        }
        if(!Uri.TryCreate(location,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.UserInfo!="" || uri.IsLoopback) throw new IOException("Укажите HTTPS-адрес JSON-манифеста.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response=await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,timeout.Token); response.EnsureSuccessStatusCode();
        await using var input=await response.Content.ReadAsStreamAsync(timeout.Token); using var bytes=new MemoryStream(); var chunk=new byte[8192]; int n;
        while((n=await input.ReadAsync(chunk,timeout.Token))>0) { if(bytes.Length+n>256*1024) throw new IOException("Слишком большой манифест."); bytes.Write(chunk,0,n); }
        return ParseManifest(Encoding.UTF8.GetString(bytes.ToArray()));
    }
    public async Task EnsureDefaultCatalogs(CancellationToken ct = default) {
        if (await repository.Setting<bool>("torrent-default-catalogs-v1",ct)) return;
        var sources=await repository.Setting<List<TorrentSearchSource>>("torrent-search-sources",ct) ?? [];
        foreach(var source in TorrentCatalogFormats.Defaults)
            if (sources.Count<20 && !sources.Any(s=>s.SearchUrl==source.SearchUrl)) sources.Add(source);
        await repository.SaveSetting("torrent-search-sources",sources,ct);
        await repository.SaveSetting("torrent-default-catalogs-v1",true,ct);
    }
    public static IReadOnlyList<string> SearchNames(AnimeApp.Models.Anime anime) =>
        new[] { anime.Name, anime.EnglishName, anime.JapaneseName, anime.RussianName }
            .Concat(anime.AlternativeNames).Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();

    public static bool HasRussianSubtitles(TorrentSearchResult result) =>
        result.SubtitleLanguages?.Any(l => new[] { "ru", "rus", "russian", "русский" }.Contains(l?.Trim().ToLowerInvariant())) == true ||
        Regex.IsMatch(result.Title, @"(?i)(\b(?:ru|rus|russian)\s*[-_.:]?\s*subs?\b|\bsubs?\s*[-_.:]?\s*(?:ru|rus|russian)\b|рус(?:ские|ский|ских)?[\s._-]*суб|субтитры[\s:._-]*рус)");

    public async Task<IReadOnlyList<TorrentSearchResult>> FindRussian(IEnumerable<string> names, CancellationToken ct, bool includeOriginalAudio = false)
    {
        var queries = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
        var errors = new System.Collections.Concurrent.ConcurrentBag<IOException>();
        var batches = await Task.WhenAll(queries.Select(async q => {
            try { return await Find(q, ct); }
            catch (IOException ex) { errors.Add(ex); return Array.Empty<TorrentSearchResult>(); }
        }));
        ct.ThrowIfCancellationRequested();
        if (queries.Length > 0 && errors.Count == queries.Length) throw errors.First();
        return batches.SelectMany(b => b).Where(r => (HasRussianSubtitles(r) || (includeOriginalAudio && r.JapaneseAudioCandidate)) && TorrentCatalogFormats.MatchesTitle(r.Title, queries)).DistinctBy(r => r.InfoHash?.Length==40 ? r.InfoHash : Regex.Match(r.Magnet, @"(?i)xt=urn:btih:([^&]+)").Success ? Regex.Match(r.Magnet, @"(?i)xt=urn:btih:([^&]+)").Groups[1].Value.ToLowerInvariant() : r.Magnet)
            .OrderByDescending(r => r.Seeds).ToArray();
    }
    public async Task<IReadOnlyList<TorrentSearchResult>> Find(string query,CancellationToken ct) {
        var sources=await repository.Setting<List<TorrentSearchSource>>("torrent-search-sources",ct) ?? [];
        if (!sources.Any(s => s.Enabled)) throw new IOException("Нет включённых каталогов раздач. Подключите источник в разделе «Источники поиска и кэш»: затем поиск по названиям аниме будет автоматическим. Также можно открыть magnet или .torrent.");
        var results=new System.Collections.Concurrent.ConcurrentBag<TorrentSearchResult>();
        var failures=0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await Task.WhenAll(sources.Where(s=>s.Enabled).Take(20).Select(async source => {
            var entered = false;
            try {
                await searchSlots.WaitAsync(timeout.Token); entered = true;
                ValidateSource(source);
                using var response=await searchHttp.GetAsync(source.SearchUrl.Replace("{query}",Uri.EscapeDataString(query)),HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                response.EnsureSuccessStatusCode();
                await using var body=await response.Content.ReadAsStreamAsync(timeout.Token);
                using var bytes=new MemoryStream(); var chunk=new byte[8192]; int count;
                while((count=await body.ReadAsync(chunk,timeout.Token))>0) { if(bytes.Length+count>2*1024*1024) throw new IOException("Слишком большой ответ источника."); bytes.Write(chunk,0,count); }
                var found=TorrentCatalogFormats.Parse(source.Format,bytes.ToArray());
                foreach (var item in found.Take(200).Where(r=>r is not null && !string.IsNullOrWhiteSpace(r.Title) && r.Title.Length<1000 && r.Magnet?.StartsWith("magnet:?",StringComparison.OrdinalIgnoreCase)==true && r.Magnet.Length<8192 && r.Size>=0 && r.Seeds>=0)) results.Add(item);
            } catch(Exception ex) when(ex is HttpRequestException or IOException or JsonException or System.Xml.XmlException or OperationCanceledException) { ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref failures); }
            finally { if (entered) searchSlots.Release(); }
        }));
        if(results.Count==0 && failures>0) throw new IOException("Источники поиска не ответили или вернули неподдерживаемый формат. Можно добавить magnet или .torrent вручную.");
        return results.DistinctBy(r=>r.Magnet).OrderByDescending(r=>r.Seeds).ToArray();
    }
    public async Task<ProviderRelease> ImportResult(TorrentSearchResult result,CancellationToken ct) {
        if(string.IsNullOrWhiteSpace(result.TorrentUrl)) return await Import(result.Magnet,result.Title,ct);
        if(!Uri.TryCreate(result.TorrentUrl,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.UserInfo!="" || uri.IsLoopback)
            throw new IOException("Некорректный адрес .torrent.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response=await searchHttp.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,timeout.Token); response.EnsureSuccessStatusCode();
        await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);
        using var bytes=new MemoryStream(); var buffer=new byte[8192]; int count;
        while((count=await stream.ReadAsync(buffer,timeout.Token))>0) { if(bytes.Length+count>8*1024*1024) throw new IOException("Слишком большой .torrent."); bytes.Write(buffer,0,count); }
        var data=bytes.ToArray(); var torrent=await MonoTorrent.Torrent.LoadAsync(data);
        if(result.InfoHash?.Length==40 && !torrent.InfoHashes.Contains(MonoTorrent.InfoHash.FromHex(result.InfoHash))) throw new IOException("Каталог вернул другую раздачу.");
        var id=Convert.ToHexString(SHA256.HashData(data));
        var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AnimeApp","TorrentInputs"); Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,id+".torrent"); await File.WriteAllBytesAsync(path,data,ct);
        await repository.SaveSetting("torrent-input-"+id,path,ct); await repository.SaveSetting("torrent-title-"+id,result.Title,ct);
        return new(id,result.Title);
    }
    public async Task<ProviderRelease> Import(string input,string title,CancellationToken ct) {
        input=input.Trim();
        byte[] identity;
        if(input.StartsWith("magnet:",StringComparison.OrdinalIgnoreCase)) { _=MonoTorrent.MagnetLink.Parse(input); identity=Encoding.UTF8.GetBytes(input); }
        else {
            if(!File.Exists(input) || !input.EndsWith(".torrent",StringComparison.OrdinalIgnoreCase) || new FileInfo(input).Length>8*1024*1024) throw new IOException("Выберите .torrent до 8 МБ или magnet-ссылку.");
            identity=await File.ReadAllBytesAsync(input,ct); _=await MonoTorrent.Torrent.LoadAsync(identity);
        }
        var id=Convert.ToHexString(SHA256.HashData(identity));
        if(!input.StartsWith("magnet:",StringComparison.OrdinalIgnoreCase)) {
            var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AnimeApp","TorrentInputs"); Directory.CreateDirectory(folder);
            input=Path.Combine(folder,id+".torrent"); await File.WriteAllBytesAsync(input,identity,ct);
        }
        await repository.SaveSetting("torrent-input-"+id,input,ct);
        await repository.SaveSetting("torrent-title-"+id,title,ct);
        return new(id,title);
    }
    public async Task<ProviderRelease> GetRelease(string id,CancellationToken ct=default) => new(id,await repository.Setting<string>("torrent-title-"+id,ct) ?? "Торрент");
    private async Task<MonoTorrent.Client.TorrentManager> Open(string id,CancellationToken ct) {
        if(!Regex.IsMatch(id,"^[A-F0-9]{64}$")) throw new IOException("Некорректная раздача.");
        var input=await repository.Setting<string>("torrent-input-"+id,ct) ?? throw new IOException("Раздача не сохранена. Добавьте её заново.");
        return await session.Open(id,input,await repository.Setting<TorrentOptions>("torrent-options",ct) ?? new(),ct);
    }
    public static int EpisodeNumber(string path) {
        var stem=Regex.Replace(Path.GetFileNameWithoutExtension(path),@"\[[^\]]*\]", " ").Trim();
        // Require a complete episode token, never a prefix of resolution or checksum.
        var match=Regex.Match(stem,@"(?:[Ss]\d{1,2}[Ee]|\b[Ee][Pp]?\.?\s*| - )(\d{1,3})(?=$|[\s._-]|v\d\b)");
        if(!match.Success) match=Regex.Match(stem,@"^(\d{1,3})(?=$|[\s._-])");
        return match.Success && int.TryParse(match.Groups[1].Value,out var n) ? n : 0;
    }

    public static bool IsVideo(string path) => new[]{".mkv",".mp4",".webm",".avi",".m4v"}.Contains(Path.GetExtension(path).ToLowerInvariant());
    public async Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id,CancellationToken ct=default) {
        if(id.StartsWith("catalog-",StringComparison.Ordinal)) return await GetSavedEpisodes(id,ct);
        if(Playing || Downloading) throw new IOException("Сначала закройте текущий просмотр.");
        var manager=await Open(id,ct);
        var episodes = manager.Files.Where(f=>IsVideo(f.Path)).OrderBy(f=>Regex.Replace(f.Path,@"\d+",m=>m.Value.PadLeft(12,'0')),StringComparer.OrdinalIgnoreCase)
            .Select(f=>new ProviderEpisode(f.Path,EpisodeNumber(f.Path),Path.GetFileName(f.Path),id,CountsTowardProgress:EpisodeNumber(f.Path)>0)).ToArray();
        await repository.SaveSetting("torrent-episodes-"+id,episodes,ct);
        return episodes;
    }
    public async Task<IReadOnlyList<ProviderEpisode>> GetSavedEpisodes(string releaseId,CancellationToken ct=default) => await repository.Setting<ProviderEpisode[]>("torrent-episodes-"+releaseId,ct) ?? [];
    public Task<PlaybackSource> GetPlayback(string releaseId,ProviderEpisode episode,CancellationToken ct=default) {
        if (Playing || Downloading) throw new IOException("Сначала закройте текущий просмотр.");
        return GetNextPlayback(releaseId,episode,ct);
    }
    public async Task<PlaybackSource> GetNextPlayback(string releaseId,ProviderEpisode episode,CancellationToken ct=default) {
        await playbackGate.WaitAsync(ct);
        try {
            if(releaseId.StartsWith("catalog-",StringComparison.Ordinal)) return await ResolveCatalogEpisode(releaseId,episode,ct);
            if(episode.ReleaseId.Length>0 && episode.ReleaseId!=releaseId) throw new IOException("Список серий изменился. Выберите серию заново.");
            await Open(releaseId,ct); return await session.Play(episode.Id,ct);
        } finally { playbackGate.Release(); }
    }
    public Task Stop(bool fromPlayer=false) { if(Playing && !fromPlayer) throw new IOException("Сначала закройте плеер."); Playing=false; return session.Stop(); }
    public Task Clear() => session.Clear();
}
