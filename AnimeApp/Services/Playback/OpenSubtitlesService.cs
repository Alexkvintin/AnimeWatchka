using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnimeApp.Services.Shikimori;

namespace AnimeApp.Services.Playback;
public sealed record OpenSubtitleCredentials(string ApiKey, string Token = "") {
    public override string ToString() => "OpenSubtitles [protected]";
}
public sealed class OpenSubtitlesService(HttpClient http, ProtectedFile<OpenSubtitleCredentials> credentials)
{
    public static string CredentialsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AnimeApp","opensubtitles.dat");
    private async Task<JsonElement> Api(string path, CancellationToken ct, object? body = null) {
        var saved=await credentials.Read(ct);
        if (string.IsNullOrWhiteSpace(saved?.ApiKey)) throw new IOException("OpenSubtitles: сохраните свой API-ключ в настройках источника.");
        using var request=new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,"https://api.opensubtitles.com/api/v1/"+path);
        request.Headers.TryAddWithoutValidation("Api-Key",saved.ApiKey);
        request.Headers.UserAgent.ParseAdd("AnimeWatchka v0.1.28");
        if (!string.IsNullOrWhiteSpace(saved.Token)) request.Headers.Authorization=new("Bearer",saved.Token);
        if (body is not null) request.Content=JsonContent.Create(body);
        using var response=await http.SendAsync(request,ct); response.EnsureSuccessStatusCode();
        var bytes=await response.Content.ReadAsByteArrayAsync(ct);
        if(bytes.Length>2*1024*1024) throw new IOException("Слишком большой ответ OpenSubtitles.");
        using var doc=JsonDocument.Parse(bytes);return doc.RootElement.Clone();
    }
    public async Task Login(string key,string username,string password,CancellationToken ct=default) {
        if(string.IsNullOrWhiteSpace(key)) throw new IOException("Введите API-ключ OpenSubtitles.");
        // Do not replace a working credential until the login has succeeded.
        var token="";
        if(!string.IsNullOrWhiteSpace(username)) {
            using var request=new HttpRequestMessage(HttpMethod.Post,"https://api.opensubtitles.com/api/v1/login") { Content=JsonContent.Create(new {username,password}) };
            request.Headers.TryAddWithoutValidation("Api-Key",key.Trim());request.Headers.UserAgent.ParseAdd("AnimeWatchka v0.1.28");
            using var response=await http.SendAsync(request,ct);response.EnsureSuccessStatusCode();
            using var doc=await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),cancellationToken:ct);
            token=doc.RootElement.GetProperty("token").GetString() ?? throw new IOException("OpenSubtitles не вернул токен.");
        }
        else {
            using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.opensubtitles.com/api/v1/infos/formats");
            request.Headers.TryAddWithoutValidation("Api-Key",key.Trim()); request.Headers.UserAgent.ParseAdd("AnimeWatchka v0.1.28");
            using var response=await http.SendAsync(request,ct); response.EnsureSuccessStatusCode();
        }
        await credentials.Write(new(key.Trim(),token),ct);
    }
    public Task Clear() => credentials.Clear();
    public async Task<bool> Configured(CancellationToken ct=default) => !string.IsNullOrWhiteSpace((await credentials.Read(ct))?.ApiKey);
    private static string Text(JsonElement e,string key) => e.TryGetProperty(key,out var v) ? v.ToString() : "";
    private static int Number(JsonElement e,string key) => int.TryParse(Text(e,key),out var n) ? n : 0;
    private static string Normalize(string text) => string.Join(" ",Regex.Matches(text.ToLowerInvariant(),@"[\p{L}\p{N}]+").Select(m=>m.Value));
    public static bool Matches(JsonElement attributes,string[] names,int year,int episode,string kind) {
        if(Text(attributes,"language") is not ("ru" or "uk" or "en") || Text(attributes,"machine_translated")=="True" || Text(attributes,"ai_translated")=="True") return false;
        if(!attributes.TryGetProperty("feature_details",out var feature)) return false;
        // Exact title + release year protects remakes and separately catalogued anime seasons.
        if(year<=0 || Number(feature,"year")!=year || !names.Any(n=>Normalize(n)==Normalize(Text(feature,"title")) || Normalize(n)==Normalize(Text(feature,"parent_title")))) return false;
        if(kind=="movie") return Text(feature,"feature_type")=="Movie";
        if(Number(feature,"episode_number")!=episode) return false;
        var seasons=names.Select(n=>Regex.Match(n,@"(?i)(?:season\s*(\d+)|(\d+)(?:st|nd|rd|th)\s+season)")).Where(m=>m.Success).Select(m=>int.Parse(m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value)).Distinct().ToArray();
        return Number(feature,"season_number")== (seasons.Length==1 ? seasons[0] : 1);
    }
    public async Task<IReadOnlyList<OnlineSubtitle>> Find(string[] names,int year,int episode,string kind,CancellationToken ct) {
        if(!await Configured(ct) || year<=0) return [];
        var results=new List<OnlineSubtitle>();
        foreach(var name in names.Where(n=>!string.IsNullOrWhiteSpace(n)).Distinct().Take(2)) {
            var data=await Api("subtitles?languages=ru,uk,en&machine_translated=exclude&ai_translated=exclude&query="+Uri.EscapeDataString(name)+(kind=="movie"?"&type=movie":"&type=episode&episode_number="+episode),ct);
            if(!data.TryGetProperty("data",out var items)) continue;
            foreach(var entry in items.EnumerateArray()) {
                if(!entry.TryGetProperty("attributes",out var a) || !Matches(a,names,year,episode,kind) || !a.TryGetProperty("files",out var files)) continue;
                foreach(var file in files.EnumerateArray().Take(1)) {
                    var id=Number(file,"file_id"); if(id<=0) continue;
                    var author=a.TryGetProperty("uploader",out var uploader) ? Text(uploader,"name") : "Перевод";
                    results.Add(new(id,Text(a,"language"),author,0,"OpenSubtitles","srt"));
                }
            }
            if(results.Count>0) break;
        }
        return results.DistinctBy(r=>r.Id).OrderByDescending(r=>r.Language==MediaLanguage.Default).Take(5).ToArray();
    }
    public async Task<string> DownloadUrl(long fileId,CancellationToken ct) {
        var data=await Api("download",ct,new {file_id=fileId,sub_format="srt"});
        var link=Text(data,"link");
        if(!Uri.TryCreate(link,UriKind.Absolute,out var uri) || uri.Scheme!="https" || !(uri.Host=="opensubtitles.com" || uri.Host.EndsWith(".opensubtitles.com",StringComparison.OrdinalIgnoreCase))) throw new IOException("Некорректный адрес субтитров OpenSubtitles.");
        return link;
    }
}
