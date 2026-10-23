using System.Text.RegularExpressions;
using MonoTorrent;
using MonoTorrent.Client;
using AnimeApp.Services.Playback;

namespace AnimeApp.Services.Torrents;
public record TorrentOptions(int DownloadKiB = 0, int UploadKiB = 256);
public sealed class TorrentSession : IAsyncDisposable, IDisposable
{
    private readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeApp", "TorrentCache");
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private ClientEngine? engine;
    private TorrentManager? manager;
    private string? activeId;
    private TorrentHttpStream? http;
    public string Status => manager is null ? "Торрент остановлен" : $"{(manager.State == TorrentState.Error ? "Ошибка торрент-сети" : manager.State == TorrentState.Seeding ? "Файл загружен" : manager.Monitor.DownloadSpeed == 0 ? "Ожидание данных от участников" : "Загрузка фрагментов")} · {manager.Monitor.DownloadSpeed / 1024} КБ/с · {manager.Progress:0.0}%";
    public async Task<TorrentManager> Open(string id, string input, TorrentOptions options, CancellationToken ct)
    {
        if (!Regex.IsMatch(id,"^[A-F0-9]{64}$")) throw new IOException("Некорректный идентификатор раздачи.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); ct = operation.Token;
        await gate.WaitAsync(ct);
        try {
            if (manager is not null && activeId == id) return manager;
            await StopCore(keepEngine:true);
            Directory.CreateDirectory(root);
            var engineSettings = new EngineSettingsBuilder {
                CacheDirectory = Path.Combine(root,"metadata"), AllowPortForwarding = false,
                MaximumDownloadRate = Math.Clamp(options.DownloadKiB,0,1000000)*1024,
                MaximumUploadRate = Math.Clamp(options.UploadKiB,1,1000000)*1024
            }.ToSettings();
            if(engine is null) engine=new ClientEngine(engineSettings);
            else await engine.UpdateSettingsAsync(engineSettings);
            var folder = Path.Combine(root,id);
            Directory.CreateDirectory(folder);
            manager = input.StartsWith("magnet:",StringComparison.OrdinalIgnoreCase)
                ? await engine.AddStreamingAsync(MagnetLink.Parse(input),folder)
                : await engine.AddStreamingAsync(await Torrent.LoadAsync(input),folder);
            activeId = id;
            await manager.StartAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            try { await manager.WaitForMetadataAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Не удалось получить список файлов за 40 секунд. Возможно, нет доступных сидов. Повторите позже."); }
            foreach(var file in manager.Files) {
                var full = Path.GetFullPath(Path.Combine(folder,file.Path));
                if (!full.StartsWith(Path.GetFullPath(folder)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new IOException("Небезопасный путь в торренте.");
                await manager.SetFilePriorityAsync(file, Priority.DoNotDownload);
            }
            return manager;
        } catch { await StopCore(); throw; }
        finally { gate.Release(); }
    }
    public async Task<PlaybackSource> Play(string filePath, CancellationToken ct)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); ct = operation.Token;
        await gate.WaitAsync(ct);
        try {
            if (manager is null) throw new IOException("Сначала выберите раздачу.");
            if (http is not null) { await http.DisposeAsync(); http = null; }
            var file = manager.Files.FirstOrDefault(f => f.Path == filePath)
                ?? throw new IOException("Серия отсутствует в открытой раздаче. Обновите список серий.");
            foreach(var f in manager.Files) await manager.SetFilePriorityAsync(f, Priority.DoNotDownload);
            var subs = new List<MediaTrack>();
            var videoCount = manager.Files.Count(f => TorrentProvider.IsVideo(f.Path));
            using var subtitleBudget = CancellationTokenSource.CreateLinkedTokenSource(ct); subtitleBudget.CancelAfter(TimeSpan.FromSeconds(8));
            foreach (var sub in manager.Files.Where(f => f.Length <= 4*1024*1024 && new[]{".ass",".ssa",".srt",".vtt"}.Contains(Path.GetExtension(f.Path).ToLowerInvariant()) && MatchesSubtitle(file.Path, f.Path, videoCount)).Take(12)) {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(subtitleBudget.Token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try {
                    await manager.SetFilePriorityAsync(sub,Priority.Highest);
                    using var input = await manager.StreamProvider!.CreateStreamAsync(sub,false,timeout.Token);
                    using var bytes = new MemoryStream(); await input.CopyToAsync(bytes,timeout.Token);
                    var destination = Path.Combine(root,activeId!,"sidecars"); Directory.CreateDirectory(destination);
                    var path = Path.Combine(destination,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sub.Path)))+Path.GetExtension(sub.Path));
                    await File.WriteAllBytesAsync(path,bytes.ToArray(),ct); subs.Add(new(path));
                } catch(OperationCanceledException) when(!ct.IsCancellationRequested) { }
                finally { await manager.SetFilePriorityAsync(sub,Priority.DoNotDownload); }
            }
            await manager.SetFilePriorityAsync(file,Priority.Highest);
            // StreamingPieceRequester follows Stream.Seek; mpv's range requests drive piece priority.
            var stream = await manager.StreamProvider!.CreateStreamAsync(file,false,ct);
            // Metadata/seed counts do not prove that a peer can deliver video data.
            // Require the initial bytes before handing a URL to mpv.
            try {
                using var startup=CancellationTokenSource.CreateLinkedTokenSource(ct);
                startup.CancelAfter(TimeSpan.FromSeconds(12));
                var probe=new byte[64*1024];
                long buffered=0, target=Math.Min(stream.Length,512*1024);
                while(buffered<target) {
                    var read=await stream.ReadAsync(probe.AsMemory(0,(int)Math.Min(probe.Length,target-buffered)),startup.Token);
                    if(read==0) throw new IOException("Источник вернул неполное видео.");
                    buffered+=read;
                }
                stream.Seek(0,SeekOrigin.Begin);
            } catch { stream.Dispose(); throw; }
            http = new TorrentHttpStream(stream);
            return new(PlaybackKind.DirectMedia,http.Url,SubtitleTracks:subs, PreferredAudioLanguage:"ja,jpn", IsTorrentStream:true);
        } finally { gate.Release(); }
    }
    public static bool MatchesSubtitle(string video,string subtitle,int videoCount) {
        if(videoCount==1) return true;
        var stem=Path.GetFileNameWithoutExtension(video);
        var subStem=Path.GetFileNameWithoutExtension(subtitle);
        if(subStem.Equals(stem,StringComparison.OrdinalIgnoreCase) || subStem.StartsWith(stem+".",StringComparison.OrdinalIgnoreCase)) return true;
        var episode=TorrentProvider.EpisodeNumber(video);
        return episode>0 && (TorrentProvider.EpisodeNumber(subtitle)==episode || TorrentProvider.EpisodeNumber(Path.GetDirectoryName(subtitle) + ".mkv")==episode);
    }
    public async Task Stop() { await gate.WaitAsync(); try { await StopCore(); } finally { gate.Release(); } }
    private async Task StopCore(bool keepEngine=false) {
        if(http is not null) { await http.DisposeAsync(); http=null; }
        if(manager is not null) { await manager.StopAsync(); if(engine is not null) await engine.RemoveAsync(manager); manager=null; }
        if(!keepEngine) { engine?.Dispose(); engine=null; }
        activeId=null;
    }
    public async Task Clear() {
        await gate.WaitAsync(); try {
            if(manager is not null) throw new IOException("Закройте просмотр и остановите торрент перед очисткой.");
            var expected = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeApp", "TorrentCache"));
            if (Path.GetFullPath(root) != expected) throw new IOException("Недопустимый каталог кэша.");
            if(Directory.Exists(expected)) Directory.Delete(expected,true);
        } finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); await Stop(); }
    public void Dispose() { lifetime.Cancel(); Task.Run(Stop).GetAwaiter().GetResult(); }
}
