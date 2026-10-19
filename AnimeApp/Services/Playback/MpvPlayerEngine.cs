using System.Globalization;
using System.Runtime.InteropServices;

namespace AnimeApp.Services.Playback;
// Owns one libmpv client and its child-window renderer. All calls originate on the UI thread.
public sealed class MpvPlayerEngine : IPlayerEngine
{
    private nint handle;
    private bool initialized;
    private bool disposed;
    private string mode;
    public string ScalingMode => mode;

    public void SetSpeed(double speed)
    {
        if (Fallback is { } fallback)
            fallback.SetSpeed(speed);
        else
            Set("speed", Math.Clamp(speed, 0.5, 2).ToString(CultureInfo.InvariantCulture));
    }

    public void SetScaling(string value)
    {
        if (value is not ("linear" or "hq" or "anime"))
            return;
        mode = value;
        Set("scale", value == "linear" ? "bilinear" : "ewa_lanczossharp");
        Set("deband", value == "linear" ? "no" : "yes");
        if (value != "anime")
        {
            Set("glsl-shaders", "");
            shadersEnabled = false;
            shadersConfirmed = false;
        }

        UpdateShaders();
    }

    private string? error;
    private bool shadersEnabled;
    private bool shadersConfirmed;
    private DateTimeOffset lastShaderCheck;
    public VlcPlayerEngine? Fallback { get; private set; }

    public void UseFallback()
    {
        DisposeNative();
        Fallback = new VlcPlayerEngine(VideoScaling.Options("linear"))
        {
            FallbackReason = "mpv не смог создать видеовыход: используется VLC"
        };
    }

    public static readonly string[] AnimeShaders = ["Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_Soft_VL.glsl", "Anime4K_Upscale_CNN_x2_VL.glsl", "Anime4K_AutoDownscalePre_x2.glsl", "Anime4K_AutoDownscalePre_x4.glsl", "Anime4K_Upscale_CNN_x2_M.glsl"];
    public MpvPlayerEngine(string mode)
    {
        this.mode = mode;
        handle = Native.mpv_create();
        if (handle == 0)
            throw new IOException("Не удалось создать mpv.");
        try
        {
            foreach (var(key, value)in new (string, string)[]
            {
                ("config", "no"),
                ("load-scripts", "no"),
                ("ytdl", "no"),
                ("input-default-bindings", "no"),
                ("input-vo-keyboard", "no"),
                ("osc", "no"),
                ("osd-level", "0"),
                ("vo", "gpu-next"),
                ("gpu-api", "d3d11"),
                ("gpu-context", "d3d11"),
                ("hwdec", "auto-safe"),
                ("keep-open", "yes"),
                ("idle", "yes"),
                ("scale", "ewa_lanczossharp"),
                ("cscale", "ewa_lanczos"),
                ("dscale", "mitchell"),
                ("deband", "yes")
            }

            )
                Check(Native.mpv_set_option_string(handle, key, value));
        }
        catch
        {
            DisposeNative();
            throw;
        }
    }

    public void Attach(nint window)
    {
        if (initialized || disposed)
            return;
        Check(Native.mpv_set_option_string(handle, "wid", window.ToString(CultureInfo.InvariantCulture)));
        Check(Native.mpv_initialize(handle));
        initialized = true;
        SetScaling(mode);
    }

    public string Property(string name)
    {
        if (!initialized || disposed)
            return "";
        var ptr = Native.mpv_get_property_string(handle, name);
        if (ptr == 0)
            return "";
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? "";
        }
        finally
        {
            Native.mpv_free(ptr);
        }
    }

    double Number(string name) => double.TryParse(Property(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : 0;
    void Set(string key, string value)
    {
        if (initialized && !disposed)
            Check(Native.mpv_set_property_string(handle, key, value));
    }

    public string VideoInfo => Fallback is { } fallback ? fallback.FallbackReason + " · " + fallback.CodecInfo : $"Источник: {Property("width")}×{Property("height")} · {Property("video-codec")} · {Number("container-fps"):0.##} FPS\nВывод: {Property("osd-width")}×{Property("osd-height")} · {Property("current-vo")} · {(mode == "linear" ? "Bilinear" : "EWA Lanczos")}" + (shadersEnabled ? (shadersConfirmed ? " · Anime4K Mode B активен" : " · Anime4K: ожидается подтверждение шейдеров") : (mode == "anime" ? " · Anime4K выключен при масштабе 1:1 или уменьшении" : ""));

    void DrainEvents()
    {
        for (var i = 0; i < 128; i++)
        {
            var ptr = Native.mpv_wait_event(handle, 0);
            var ev = Marshal.PtrToStructure<MpvEvent>(ptr);
            if (ev.Id == 0)
                break;
            if (ev.Error < 0)
                error = "mpv: не удалось воспроизвести видео. Обновите адрес серии или выберите обычный видеорежим.";
            if (ev.Id == 7 && ev.Data != 0 && Marshal.ReadInt32(ev.Data) == 4)
                error = "mpv: источник видео недоступен. Выберите серию заново.";
        }
    }

    void UpdateShaders()
    {
        if (mode != "anime")
            return;
        var w = Number("width");
        var h = Number("height");
        var ow = Number("osd-width");
        var oh = Number("osd-height");
        var enable = w > 0 && h > 0 && Math.Min(ow / w, oh / h) > 1.01;
        if (enable == shadersEnabled)
            return;
        Set("glsl-shaders", enable ? string.Join(";", AnimeShaders.Select(f => Path.Combine(AppContext.BaseDirectory, "Shaders", f))) : "");
        shadersEnabled = enable;
        shadersConfirmed = false;
        lastShaderCheck = DateTimeOffset.MinValue;
    }

    public PlaybackSnapshot Snapshot
    {
        get
        {
            if (Fallback is { } fallback)
                return fallback.Snapshot;
            if (!initialized || disposed)
                return new(0, 0, false, CanControl: false);
            DrainEvents();
            UpdateShaders();
            if (shadersEnabled && DateTimeOffset.UtcNow - lastShaderCheck >= TimeSpan.FromSeconds(1))
            {
                shadersConfirmed = Property("vo-passes").Contains("Anime4K");
                lastShaderCheck = DateTimeOffset.UtcNow;
            }

            return new(Number("time-pos"), Number("duration"), Property("pause") == "no" && Property("idle-active") != "yes", Property("eof-reached") == "yes", error);
        }
    }

    public Task Open(PlaybackSource source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Fallback is { } fallback)
            return fallback.Open(source, ct);
        if (!initialized)
            throw new IOException("Окно mpv ещё не готово.");
        if (source.Kind == PlaybackKind.WebPage)
            throw new ArgumentException("mpv не открывает веб-страницы.");
        string Validate(string location)
        {
            if (source.Kind == PlaybackKind.LocalFile && File.Exists(location))
                return Path.GetFullPath(location);
            if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme == "https")
                return location;
            throw new IOException("Недопустимый адрес медиадорожки.");
        }

        error = null;
        Set("http-header-fields", "");
        if (source.Headers?.TryGetValue("Referer", out var referer) == true && !referer.Contains('\n') && !referer.Contains('\r'))
            Set("referrer", referer);
        else
            Set("referrer", "");
        Set("audio-files", source.AudioTrack is { } audio ? Validate(audio.Location) : "");
        Set("sub-files", string.Join(";", (source.SubtitleTracks ?? []).Select(t => Validate(t.Location))));
        Set("start", Math.Max(0, source.StartSeconds).ToString(CultureInfo.InvariantCulture));
        Set("pause", source.StartPaused ? "yes" : "no");
        Command("loadfile", Validate(source.VideoTrack?.Location ?? source.Location), "replace");
        return Task.CompletedTask;
    }

    public void Command(params string[] args)
    {
        if (!initialized || disposed)
            return;
        var strings = args.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var array = Marshal.AllocHGlobal((strings.Length + 1) * IntPtr.Size);
        try
        {
            for (int i = 0; i < strings.Length; i++)
                Marshal.WriteIntPtr(array, i * IntPtr.Size, strings[i]);
            Marshal.WriteIntPtr(array, strings.Length * IntPtr.Size, 0);
            Check(Native.mpv_command(handle, array));
        }
        finally
        {
            Marshal.FreeHGlobal(array);
            foreach (var p in strings)
                Marshal.FreeCoTaskMem(p);
        }
    }

    public void Play()
    {
        if (Fallback is { } f)
            f.Play();
        else
            Set("pause", "no");
    }

    public void Pause()
    {
        if (Fallback is { } f)
            f.Pause();
        else
            Set("pause", "yes");
    }

    public void Seek(double seconds)
    {
        if (Fallback is { } f)
            f.Seek(seconds);
        else
            Command("seek", Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture), "absolute+exact");
    }

    public void SetVolume(int volume)
    {
        if (Fallback is { } f)
            f.SetVolume(volume);
        else
            Set("volume", Math.Clamp(volume, 0, 100).ToString(CultureInfo.InvariantCulture));
    }

    public void DisposeNative()
    {
        if (disposed)
            return;
        disposed = true;
        if (handle != 0)
        {
            Native.mpv_terminate_destroy(handle);
            handle = 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        DisposeNative();
        if (Fallback is { } f)
        {
            Fallback = null;
            await f.DisposeAsync();
        }
    }

    static void Check(int code)
    {
        if (code < 0)
            throw new IOException("mpv: ошибка настройки или команды (" + code + ").");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MpvEvent
    {
        public int Id;
        public int Error;
        public ulong Reply;
        public nint Data;
    }

    static class Native
    {
        const string Dll = "libmpv-2.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint mpv_create();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int mpv_initialize(nint h);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void mpv_terminate_destroy(nint h);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int mpv_set_option_string(nint h, [MarshalAs(UnmanagedType.LPUTF8Str)] string n, [MarshalAs(UnmanagedType.LPUTF8Str)] string v);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int mpv_set_property_string(nint h, [MarshalAs(UnmanagedType.LPUTF8Str)] string n, [MarshalAs(UnmanagedType.LPUTF8Str)] string v);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint mpv_get_property_string(nint h, [MarshalAs(UnmanagedType.LPUTF8Str)] string n);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void mpv_free(nint p);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int mpv_command(nint h, nint args);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint mpv_wait_event(nint h, double timeout);
    }
}
