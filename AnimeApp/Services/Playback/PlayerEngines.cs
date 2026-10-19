using LibVLCSharp.Shared;

namespace AnimeApp.Services.Playback;
public record PlaybackSnapshot(double Position, double Duration, bool Playing, bool Ended = false, string? Error = null, bool CanControl = true, bool Seeking = false);
public interface IPlayerEngine : IAsyncDisposable
{
    PlaybackSnapshot Snapshot { get; }

    Task Open(PlaybackSource source, CancellationToken ct = default);
    void Play();
    void Pause();
    void Seek(double seconds);
    void SetVolume(int volume);
    void SetSpeed(double speed)
    {
    }
}

public sealed class VlcPlayerEngine : IPlayerEngine
{
    public void SetSpeed(double speed) => Player.SetRate((float)Math.Clamp(speed, 0.5, 2));
    private readonly LibVLC vlc;
    public MediaPlayer Player { get; }
    public string ScalerInfo { get; private set; } = "Ожидание видеовыхода";

    public string CodecInfo
    {
        get
        {
            using var media = Player.Media;
            var track = media?.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
            if (track is not { } t)
                return "";
            var fps = t.Data.Video.FrameRateDen > 0 ? (double)t.Data.Video.FrameRateNum / t.Data.Video.FrameRateDen : 0;
            return media!.CodecDescription(TrackType.Video, t.Codec) + (fps > 0 ? $" · {fps:0.##} FPS" : "") + (t.Bitrate > 0 ? $" · {t.Bitrate / 1000} kbit/s" : "");
        }
    }

    public string FallbackReason { get; init; } = "";

    private bool failed;
    private double lastDuration;

    public VlcPlayerEngine(params string[] options)
    {
        Core.Initialize();
        vlc = new LibVLC(true, new[] { "--no-video-title-show", "--no-sub-autodetect-file" }.Concat(options).ToArray());
        vlc.Log += (_, e) =>
        {
            // Keep only a fixed renderer label, never URLs or other native log data.
            if (ScalerInfo == "Ожидание видеовыхода" && e.Message.Contains("using vout display module \"direct3d11\""))
                ScalerInfo = "Direct3D11 · Linear Sampler";
            if (e.Message.Contains("Using Linear Sampler scaler"))
                ScalerInfo = "Direct3D11 · Linear Sampler";
            if (e.Message.Contains("Using Video Processor scaler"))
                ScalerInfo = "Direct3D11 · Video Processor";
            if (e.Message.Contains("Using Super Resolution scaler"))
                ScalerInfo = "Direct3D11 · Super Resolution: ожидается подтверждение активации";
            if (e.Message.Contains("turning VSR ON"))
                ScalerInfo = "Direct3D11 · Super Resolution активен";
            if (e.Message.Contains("turning VSR OFF"))
                ScalerInfo = "Direct3D11 · Super Resolution выключен (масштаб 1:1 или уменьшение)";
            if (e.Message.Contains("Failed to (re)initialize scaler") || e.Message.Contains("Failed to set the Intel VPE") || e.Message.Contains("Failed to set the NVIDIA video process"))
                ScalerInfo = "Direct3D11 · улучшение недоступно, обычный вывод";
        };
        Player = new MediaPlayer(vlc);
        Player.EnableMouseInput = false;
        Player.EnableKeyInput = false;
        Player.EncounteredError += (_, _) => failed = true;
    }

    public PlaybackSnapshot Snapshot
    {
        get
        {
            var duration = Math.Max(0, Player.Length / 1000d);
            if (duration > 0)
                lastDuration = duration;
            var ended = Player.State == VLCState.Ended;
            return new(ended ? lastDuration : Math.Max(0, Player.Time / 1000d), lastDuration, Player.IsPlaying, ended, failed ? "Не удалось воспроизвести видео. Проверьте интернет или выберите серию заново, чтобы обновить адрес." : null);
        }
    }

    public async Task Open(PlaybackSource s, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        failed = false;
        lastDuration = 0;

        if (s.Kind == PlaybackKind.WebPage)
            throw new InvalidOperationException("WebPage requires a browser adapter.");
        s = s with
        {
            Location = s.VideoTrack?.Location ?? s.Location
        };
        if (s.Kind == PlaybackKind.LocalFile && !File.Exists(s.Location))
            throw new FileNotFoundException("Видео не найдено.");
        if (s.Kind == PlaybackKind.DirectMedia && (!Uri.TryCreate(s.Location, UriKind.Absolute, out var remote) || remote.Scheme != "https"))
            throw new InvalidOperationException("Неподдерживаемый адрес видео.");
        using var media = new Media(vlc, s.Kind == PlaybackKind.LocalFile ? new Uri(Path.GetFullPath(s.Location)) : new Uri(s.Location));
        if (s.Headers?.TryGetValue("Referer", out var referer) == true && !referer.Contains('\n') && !referer.Contains('\r'))
            media.AddOption(":http-referrer=" + referer);
        string Track(string location)
        {
            if (s.Kind == PlaybackKind.LocalFile && File.Exists(location))
                return new Uri(Path.GetFullPath(location)).ToString();
            if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme == "https")
                return uri.ToString();
            throw new IOException("Недопустимый адрес дополнительной дорожки.");
        }

        if (s.AudioTrack is { } audio)
            media.AddSlave(MediaSlaveType.Audio, 4, Track(audio.Location));
        foreach (var subtitle in s.SubtitleTracks ?? [])
            media.AddSlave(MediaSlaveType.Subtitle, 4, Track(subtitle.Location));
        media.AddOption(":start-time=" + Math.Max(0, s.StartSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));

        media.AddOption(":network-caching=1500");
        var mute = Player.Mute;
        if (s.StartPaused) Player.Mute = true;
        try
        {
            if (!Player.Play(media)) throw new IOException("Не удалось запустить проигрыватель.");
            if (s.StartSeconds > 0 || s.StartPaused)
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
                while (Player.Length <= 0 || !Player.IsPlaying || Math.Abs(Player.Time / 1000d - s.StartSeconds) > 3)
                {
                    ct.ThrowIfCancellationRequested();
                    if (failed || DateTimeOffset.UtcNow >= deadline) throw new IOException("Не удалось восстановить позицию видео.");
                    await Task.Delay(100, ct);
                }
                if (s.StartPaused) Player.SetPause(true);
            }
        }
        catch { Player.SetPause(true); throw; }
        finally { if (s.StartPaused) Player.Mute = mute; }
    }

    public void Play() => Player.SetPause(false);
    public void Pause() => Player.SetPause(true);
    public void Seek(double seconds)
    {
        if (Player.IsSeekable)
            Player.Time = (long)(Math.Max(0, seconds) * 1000);
    }

    public void SetVolume(int volume) => Player.Volume = Math.Clamp(volume, 0, 100);
    public async ValueTask DisposeAsync()
    {
        await Task.Run(() => Player.Stop());
        Player.Dispose();
        vlc.Dispose();
    }
}

// The WebView host supplies commands/events; the playback core is unaware of provider HTML.
public sealed class WebPlayerEngine : IPlayerEngine
{
    public void SetSpeed(double speed) => Command("speed(" + Math.Clamp(speed, 0.5, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
    public Func<string, Task>? Execute { get; set; }
    public Func<string, Task>? Navigate { get; set; }
    public PlaybackSnapshot Snapshot { get; private set; } = new(0, 0, false, CanControl: false);

    public void Update(PlaybackSnapshot snapshot) => Snapshot = snapshot;
    public Task Open(PlaybackSource s, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(s.Location, UriKind.Absolute, out var u) || u.Scheme != "https")
            throw new InvalidOperationException("Веб-плеер требует HTTPS.");
        Snapshot = new(0, 0, false, CanControl: false);
        return Navigate?.Invoke(s.Location) ?? throw new InvalidOperationException("WebView ещё не готов.");
    }

    private void Command(string command)
    {
        if (Execute is not null)
            _ = Execute("window.animeAppMedia?." + command);
    }

    public void Play() => Command("play()");
    public void Pause() => Command("pause()");
    public void Seek(double seconds) => Command("seek(" + Math.Max(0, seconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
    public void SetVolume(int volume) => Command("volume(" + (Math.Clamp(volume, 0, 100) / 100d).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
    public ValueTask DisposeAsync()
    {
        Execute = null;
        Navigate = null;
        return ValueTask.CompletedTask;
    }
}

public static class PlayerEngineFactory
{
    internal static bool HasRequiredVulkanExports(Func<string, bool> hasExport) =>
        hasExport("vkGetPhysicalDeviceProperties2") && hasExport("vkGetInstanceProcAddr");
    private static bool HasOutdatedVulkanLoader()
    {
        if (!OperatingSystem.IsWindows()) return false;
        // Older vendor loaders can fail in the Windows loader before mpv's managed fallback.
        var path = Path.Combine(Environment.SystemDirectory, "vulkan-1.dll");
        if (!File.Exists(path) || !System.Runtime.InteropServices.NativeLibrary.TryLoad(path, out var handle)) return false;
        try { return !HasRequiredVulkanExports(name => System.Runtime.InteropServices.NativeLibrary.TryGetExport(handle, name, out _)); }
        finally { System.Runtime.InteropServices.NativeLibrary.Free(handle); }
    }
    private static IPlayerEngine CreateDirect(string scaling, bool compatibility)
    {
        // Avoid loading libmpv at all: missing driver entry points can fail before managed fallback.
        if (compatibility)
            return new VlcPlayerEngine("--avcodec-hw=none", "--vout=direct3d9")
            { FallbackReason = "Совместимый режим · VLC · программное декодирование" };
        if (HasOutdatedVulkanLoader())
            return new VlcPlayerEngine("--avcodec-hw=none", "--vout=direct3d9")
            { FallbackReason = "Старый Vulkan-драйвер · совместимый режим VLC. Для Anime4K требуется обновление графического драйвера." };
        if (scaling is "linear" or "hq" or "anime")
        {
            try
            {
                return new MpvPlayerEngine(scaling);
            }
            catch (Exception ex)when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or IOException)
            {
                return new VlcPlayerEngine(VideoScaling.Options("linear"))
                {
                    FallbackReason = "mpv недоступен: обычный режим VLC"
                };
            }
        }

        return new VlcPlayerEngine(VideoScaling.Options(scaling));
    }

    public static IPlayerEngine Create(PlaybackKind kind, string scaling = "linear", bool compatibility = false) => kind switch
    {
        PlaybackKind.DirectMedia or PlaybackKind.LocalFile => CreateDirect(scaling, compatibility),
        PlaybackKind.WebPage => new WebPlayerEngine(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))};
}
