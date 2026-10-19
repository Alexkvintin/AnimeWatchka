using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace AnimeApp.Services.Playback;

public record DownloadProgress(long Bytes, long? Total, int Parts = 0, int TotalParts = 0)
{
    public double Percent => TotalParts > 0 ? Parts * 100d / TotalParts : Total > 0 ? Math.Min(100, Bytes * 100d / Total.Value) : 0;
    public string Label => $"{Bytes / 1048576d:0.0} МБ" + (TotalParts > 0 ? $" · фрагменты {Parts}/{TotalParts}" : Total > 0 ? $" из {Total / 1048576d:0.0} МБ" : "");
}
public record DownloadResult(string Directory, string MediaFile);
public sealed class EpisodeDownloader(HttpClient http)
{
    public const string PendingName = ".animewatchka-partial-";
    public static int RemoveInterrupted(string root)
    {
        root = Path.GetFullPath(root);
        int removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(root, PendingName + "*"))
        {
            if (!Regex.IsMatch(Path.GetFileName(directory), @"^\.animewatchka-partial-[a-f0-9]{32}$")) continue;
            var full = Path.GetFullPath(directory);
            if (Path.GetDirectoryName(full) != root.TrimEnd(Path.DirectorySeparatorChar) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateDirectories(full).Any()) continue;
            var marker = Path.Combine(full, "download.pending");
            if (!File.Exists(marker) || new FileInfo(marker).Length > 100) continue;
            try { using var probe = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None); using var reader = new StreamReader(probe); if (reader.ReadToEnd() != "AnimeWatchka download v1") continue; }
            catch (IOException) { continue; } // Another app instance is still downloading here.
            var files = Directory.GetFiles(full);
            if (files.Any(f => (File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0)) continue;
            foreach (var file in files) File.Delete(file);
            Directory.Delete(full); removed++;
        }
        return removed;
    }
    internal static MediaVariant? SelectQuality(IReadOnlyList<MediaVariant>? variants, string quality)
    {
        if (variants is null || variants.Count == 0) return null;
        if (!int.TryParse(quality.TrimEnd('p'), out var height)) return QualityNegotiation.Select(variants);
        return variants.Where(v => v.Height > 0 && v.Height <= height).OrderByDescending(v => v.Height).FirstOrDefault()
            ?? variants.OrderBy(v => v.Height > 0 ? v.Height : int.MaxValue).First();
    }
    private static Uri Https(string location) => Uri.TryCreate(location, UriKind.Absolute, out var u) && u.Scheme == "https" && string.IsNullOrEmpty(u.UserInfo) ? u : throw new InvalidDataException("Источник не предоставляет подходящий HTTPS-поток.");
    public async Task<DownloadResult> Download(PlaybackSource source, string destination, string title, string quality, IProgress<DownloadProgress>? progress, CancellationToken ct, int episode = 1, DownloadIdentity? identity = null)
    {
        if (source.Kind != PlaybackKind.DirectMedia || source.AudioTrack is not null || source.SubtitleTracks?.Count > 0)
            throw new InvalidDataException("Скачивание этого источника или отдельных дорожек пока не поддерживается.");
        var location = SelectQuality(source.Variants, quality)?.Location ?? source.VideoTrack?.Location ?? source.Location;
        var uri = Https(location);
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(destination, PendingName + id);
        var name = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length > 90) name = name[..90];
        var final = Path.Combine(destination, "AnimeWatchka - " + (identity is null ? name : $"Episode {episode:D3}") + " - " + id[..8]);
        Directory.CreateDirectory(staging);
        using var markerLock = new FileStream(Path.Combine(staging, "download.pending"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        markerLock.Write(Encoding.UTF8.GetBytes("AnimeWatchka download v1")); markerLock.Flush();
        long bytes = 0;
        async Task<HttpResponseMessage> Get(Uri address)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Https(address.ToString()));
            if (source.Headers is { } headers)
                foreach (var pair in headers.Where(h => h.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)))
                    request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            try { response.EnsureSuccessStatusCode(); Https(response.RequestMessage?.RequestUri?.ToString() ?? address.ToString()); return response; }
            catch { response.Dispose(); throw; }
        }
        async Task Copy(HttpResponseMessage response, string file, int part, int parts)
        {
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Contains("html") || type.Contains("json")) throw new InvalidDataException("Источник вернул страницу вместо видео. Попробуйте заново или выберите другой источник.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(Path.Combine(staging, file), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920]; long received = 0; int count;
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
                count = await input.ReadAsync(buffer, timeout.Token);
                if (count == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, count), ct); received += count; Interlocked.Add(ref bytes, count);
                progress?.Report(new(bytes, parts > 0 ? null : response.Content.Headers.ContentLength, part, parts));
            }
            if (received == 0 || response.Content.Headers.ContentLength is long expected && received != expected) throw new IOException("Неполный ответ сервера.");
        }
        async Task<string> Manifest(HttpResponseMessage response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var data = new MemoryStream(); var buffer = new byte[8192]; int read;
            while (true) { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45)); read = await stream.ReadAsync(buffer, timeout.Token); if (read == 0) break; if (data.Length + read > 2_000_000) throw new InvalidDataException("Слишком большой плейлист."); data.Write(buffer, 0, read); }
            var text = Encoding.UTF8.GetString(data.ToArray()).TrimStart('\uFEFF');
            if (!text.StartsWith("#EXTM3U")) throw new InvalidDataException("Неверный HLS-плейлист.");
            return text;
        }
        try
        {
            string media;
            using var response = await Get(uri);
            uri = response.RequestMessage?.RequestUri ?? uri;
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
            {
                var text = await Manifest(response);
                for (int depth = 0; text.Contains("#EXT-X-STREAM-INF:"); depth++)
                {
                    if (depth >= 3 || text.Contains("#EXT-X-MEDIA:")) throw new InvalidDataException("Этот HLS-поток с отдельными дорожками пока нельзя скачать.");
                    var variant = SelectQuality(QualityNegotiation.ParseMaster(text, uri), quality) ?? throw new InvalidDataException("Не удалось выбрать качество HLS.");
                    uri = Https(variant.Location); using var child = await Get(uri); uri = child.RequestMessage?.RequestUri ?? uri; text = await Manifest(child);
                }
                var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
                if (!lines.Contains("#EXT-X-ENDLIST")) throw new InvalidDataException("Прямые эфиры и незавершённые HLS-плейлисты не скачиваются.");
                if (lines.Any(l => l.StartsWith("#EXT-X-KEY:") && l != "#EXT-X-KEY:METHOD=NONE" || l.Contains("BYTERANGE") || l.StartsWith("#EXT-X-GAP") || l.StartsWith("#EXT-X-PART") || l.StartsWith("#EXT-X-DEFINE")))
                    throw new InvalidDataException("Скачивание защищённых или сложных HLS-потоков пока не поддерживается.");
                var resources = new List<(Uri Url, string Name)>(); var local = new List<string>();
                foreach (var line in lines)
                {
                    if (!line.StartsWith('#'))
                    {
                        var namePart = $"segment-{resources.Count:D5}." + (line.Split('?')[0].EndsWith(".m4s") ? "m4s" : "ts");
                        resources.Add((Https(new Uri(uri, line).ToString()), namePart)); local.Add(namePart);
                    }
                    else if (line.StartsWith("#EXT-X-MAP:"))
                    {
                        var match = Regex.Match(line, "URI=\"([^\"]+)\"");
                        if (!match.Success) throw new InvalidDataException("Неверный заголовок HLS.");
                        var file = $"init-{resources.Count:D5}.mp4"; resources.Add((Https(new Uri(uri, match.Groups[1].Value).ToString()), file)); local.Add($"#EXT-X-MAP:URI=\"{file}\"");
                    }
                    // Only known, non-fetching tags are retained in the offline playlist.
                    else if (line == "#EXTM3U" || line == "#EXT-X-ENDLIST" || line == "#EXT-X-DISCONTINUITY" || line == "#EXT-X-INDEPENDENT-SEGMENTS" || line.StartsWith("#EXTINF:") || line.StartsWith("#EXT-X-VERSION:") || line.StartsWith("#EXT-X-TARGETDURATION:") || line.StartsWith("#EXT-X-MEDIA-SEQUENCE:") || line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE:")) local.Add(line);
                }
                if (!lines.Any(l => !l.StartsWith('#')) || resources.Count is 0 or > 20000) throw new InvalidDataException("Неподдерживаемый размер HLS-плейлиста.");
                int completed = 0;
                await Parallel.ForEachAsync(resources, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (resource, token) =>
                {
                    for (var attempt = 0; ; attempt++)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            using var part = await Get(resource.Url);
                            await Copy(part, resource.Name, Volatile.Read(ref completed), resources.Count);
                            progress?.Report(new(Interlocked.Read(ref bytes), null, Interlocked.Increment(ref completed), resources.Count));
                            break;
                        }
                        catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested && (ex is IOException or HttpRequestException or OperationCanceledException))
                        {
                            var partial = Path.Combine(staging, resource.Name);
                            if (File.Exists(partial)) { Interlocked.Add(ref bytes, -new FileInfo(partial).Length); File.Delete(partial); }
                            await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token);
                        }
                    }
                });
                media = "episode.m3u8";
                await File.WriteAllLinesAsync(Path.Combine(staging, media), local, new UTF8Encoding(false), ct);
            }
            else
            {
                var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
                if (extension is not (".mp4" or ".webm" or ".mkv") && type is not ("video/mp4" or "video/webm" or "video/x-matroska")) throw new InvalidDataException("Источник не предоставляет поддерживаемый видеофайл.");
                media = "episode" + (extension is ".webm" or ".mkv" ? extension : type == "video/webm" ? ".webm" : ".mp4");
                await Copy(response, media, 0, 0);
                using var check = File.OpenRead(Path.Combine(staging, media));
                var signature = new byte[16]; var length = check.Read(signature);
                if (length < 12 || !(Encoding.ASCII.GetString(signature, 4, 4) == "ftyp" || signature[0] == 0x1a && signature[1] == 0x45 && signature[2] == 0xdf && signature[3] == 0xa3))
                    throw new InvalidDataException("Полученный файл не похож на видео. Попробуйте другой источник.");
            }
            ct.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(staging, "download.json"), JsonSerializer.Serialize(new SavedEpisode(title, media, episode, identity)), ct);
            markerLock.Dispose();
            File.Delete(Path.Combine(staging, "download.pending"));
            Directory.Move(staging, final);
            return new(final, Path.Combine(final, media));
        }
        finally { markerLock.Dispose(); if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}


