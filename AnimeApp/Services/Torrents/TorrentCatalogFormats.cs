using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AnimeApp.Services.Torrents;

// Native data adapters only: no extension scripts or third-party executable code.
public static class TorrentCatalogFormats
{
    public static IReadOnlyList<TorrentSearchSource> Defaults => [
        new("Nyaa · японский звук", "https://nyaa.si/?page=rss&c=1_2&q={query}", Format: "nyaa"),
        new("AnimeTosho", "https://animetosho.org/feed/json?q={query}", Format: "animetosho")
    ];
    public static IReadOnlyList<TorrentSearchResult> Parse(string format, byte[] data)
    {
        if (format == "json") return JsonSerializer.Deserialize<List<TorrentSearchResult>>(data, new JsonSerializerOptions { PropertyNameCaseInsensitive=true }) ?? [];
        if (format == "nyaa") {
            using var stream = new MemoryStream(data);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit, XmlResolver=null, MaxCharactersInDocument=2*1024*1024 });
            return XDocument.Load(reader).Descendants("item").Take(200).Select(item => {
                string Field(string name) => item.Elements().FirstOrDefault(e => e.Name.LocalName==name)?.Value ?? "";
                return Entry(Field("title"), Field("infoHash"), Size(Field("size")), int.TryParse(Field("seeders"),out var n) ? n : 0, Field("link"));
            }).Where(r => r is not null).Cast<TorrentSearchResult>().ToArray();
        }
        if (format == "animetosho") {
            using var doc=JsonDocument.Parse(data);
            if(doc.RootElement.ValueKind!=JsonValueKind.Array) throw new IOException("AnimeTosho вернул неподдерживаемый ответ.");
            return doc.RootElement.EnumerateArray().Take(200).Select(e => {
                string Field(string name) => e.TryGetProperty(name,out var value) ? value.ToString() : "";
                return Entry(Field("title"), Field("info_hash"), long.TryParse(Field("total_size"),out var size) ? size : 0, int.TryParse(Field("seeders"),out var seeds) ? seeds : 0, Field("torrent_url"), Field("magnet_uri"));
            }).Where(r => r is not null).Cast<TorrentSearchResult>().ToArray();
        }
        throw new IOException("Неизвестный формат каталога.");
    }
    private static TorrentSearchResult? Entry(string title,string hash,long size,int seeds,string torrentUrl="",string magnet="")
    {
        if (!Regex.IsMatch(hash,@"\A[a-fA-F0-9]{40}\z") || string.IsNullOrWhiteSpace(title)) return null;
        // Hard subtitles cannot be replaced; a dub-only release is not an original audio candidate.
        if (Regex.IsMatch(title,@"(?i)hard[ ._-]*sub") || (Regex.IsMatch(title,@"(?i)\b(?:english|eng|russian|rus)[ ._-]*dub\b") && !Regex.IsMatch(title,@"(?i)dual[ ._-]*audio|multi[ ._-]*audio"))) return null;
        var quality=Regex.Match(title,@"\b(?:2160|1080|720|480)[pi]\b",RegexOptions.IgnoreCase).Value;
        var episodeTitle=Regex.Replace(title,@"\[[^\]]*\]", " ");
        var episode=Regex.Match(episodeTitle,@"(?: - |\bS\d{1,2}E)(\d{1,3})(?![\p{L}\p{N}])(?:\s*[-~–]\s*(\d{1,3})(?![\p{L}\p{N}]))?",RegexOptions.IgnoreCase);
        return new TorrentSearchResult(title,"magnet:?xt=urn:btih:"+hash.ToLowerInvariant()+"&dn="+Uri.EscapeDataString(title),quality,Math.Max(0,size),Math.Clamp(seeds,0,29999),episode.Success ? episode.Groups[1].Value+(episode.Groups[2].Success ? "-"+episode.Groups[2].Value : "") : "уточняются после открытия", JapaneseAudioCandidate:true, TorrentUrl:torrentUrl, InfoHash:hash.ToLowerInvariant()) with { Magnet=magnet.StartsWith("magnet:?",StringComparison.OrdinalIgnoreCase) ? magnet : "magnet:?xt=urn:btih:"+hash.ToLowerInvariant()+"&dn="+Uri.EscapeDataString(title) };
    }
    public static long Size(string value) {
        var match=Regex.Match(value,@"([\d.]+)\s*([KMGT]?i?B)",RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)) return 0;
        var power="BKMGT".IndexOf(char.ToUpperInvariant(match.Groups[2].Value[0]));
        return (long)Math.Clamp(n*Math.Pow(1024,Math.Max(0,power)),0,long.MaxValue);
    }
    public static bool MatchesTitle(string release, IEnumerable<string> names) {
        static string NormalizeSeason(string s) {
            s=Regex.Replace(s,@"(?i)\b(\d+)(?:st|nd|rd|th)\s+season\b","season $1");
            return Regex.Replace(s,@"(?i)\bS0*(\d{1,2})(?:E\d{1,3})?\b","season $1");
        }
        static int Season(string s) {
            var match=Regex.Match(NormalizeSeason(s),@"(?i)\bseason\s+(\d+)\b");
            return match.Success ? int.Parse(match.Groups[1].Value) : 1;
        }
        static string[] Words(string s) => Regex.Matches(NormalizeSeason(s).ToLowerInvariant(),@"[\p{L}\p{N}]+").Select(m=>m.Value).ToArray();
        static string TitlePart(string value) {
            value=Regex.Replace(value,@"\[[^\]]*\]", " ");
            value=Regex.Split(value,@"(?i)\s+-\s+\d|\bS\d{1,2}E\d|\b(?:1080|720|480|2160)[pi]\b")[0];
            return value;
        }
        var words=Words(release).ToHashSet();
        var titleWords=Words(TitlePart(release));
        return names.Where(n=>Season(n)==Season(release)).Any(n=> {
            var tokens=Words(TitlePart(n));
            // A short franchise title must not silently match a named sequel.
            return tokens.Length>0 && tokens.All(words.Contains) &&
                !titleWords.Except(tokens).Any(w=>!int.TryParse(w,out _) && w is not ("season" or "batch" or "complete" or "dual" or "audio" or "multi" or "sub" or "subs"));
        });
    }
    public static bool CoversEpisode(TorrentSearchResult release, int episode) {
        var range=Regex.Match(release.Episodes,@"\A(\d{1,3})(?:-(\d{1,3}))?\z");
        if(!range.Success || episode<=0) return false;
        var first=int.Parse(range.Groups[1].Value);
        var last=range.Groups[2].Success ? int.Parse(range.Groups[2].Value) : first;
        return first<=episode && episode<=last;
    }
}
