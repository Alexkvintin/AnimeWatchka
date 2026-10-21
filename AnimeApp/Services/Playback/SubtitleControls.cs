using System.Globalization;
using LibVLCSharp.Shared;

namespace AnimeApp.Services.Playback;

public record SubtitleChoice(int Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Separate subtitle tracks only; burned-in captions are part of the video.</summary>
public static class SubtitleControls
{
    private static IPlayerEngine Active(IPlayerEngine engine) =>
        engine is MpvPlayerEngine { Fallback: { } fallback } ? fallback : engine;

    public static bool Supported(IPlayerEngine engine) => Active(engine) is MpvPlayerEngine or VlcPlayerEngine;

    public static bool CanStyle(IPlayerEngine engine) => Active(engine) is MpvPlayerEngine;
    public static void Style(IPlayerEngine engine, SubtitleFont font, double size, bool preserveAss)
    {
        if (Active(engine) is not MpvPlayerEngine mpv) return;
        mpv.Command("set", "sub-font", font.Family);
        mpv.Command("set", "sub-font-size", Math.Clamp(double.IsFinite(size) ? size : 40, 16, 80).ToString(CultureInfo.InvariantCulture));
        mpv.Command("set", "sub-ass-override", preserveAss ? "no" : "force");
    }

    public static IReadOnlyList<SubtitleChoice> Tracks(IPlayerEngine engine)
    {
        var result = new List<SubtitleChoice> { new(-1, "Выключены") };
        switch (Active(engine))
        {
            case VlcPlayerEngine vlc:
                result.AddRange(vlc.Player.SpuDescription.Where(t => t.Id >= 0)
                    .Select(t => new SubtitleChoice(t.Id, string.IsNullOrWhiteSpace(t.Name) ? $"Дорожка {t.Id}" : t.Name)));
                break;
            case MpvPlayerEngine mpv:
                if (!int.TryParse(mpv.Property("track-list/count"), out var count)) break;
                for (var i = 0; i < Math.Min(count, 256); i++)
                {
                    var prefix = $"track-list/{i}/";
                    if (mpv.Property(prefix + "type") != "sub" || !int.TryParse(mpv.Property(prefix + "id"), out var id)) continue;
                    var label = string.Join(" · ", new[] { mpv.Property(prefix + "lang"), mpv.Property(prefix + "title") }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    result.Add(new(id, label.Length > 0 ? label : $"Дорожка {id}"));
                }
                break;
        }
        return result;
    }

    public static int Selected(IPlayerEngine engine) => Active(engine) switch
    {
        VlcPlayerEngine vlc => vlc.Player.Spu,
        MpvPlayerEngine mpv when int.TryParse(mpv.Property("sid"), out var id) => id,
        _ => -1
    };

    public static void Select(IPlayerEngine engine, int id)
    {
        switch (Active(engine))
        {
            case VlcPlayerEngine vlc: vlc.Player.SetSpu(id); break;
            case MpvPlayerEngine mpv: mpv.Command("set", "sid", id < 0 ? "no" : id.ToString(CultureInfo.InvariantCulture)); break;
        }
    }

    public static void Delay(IPlayerEngine engine, double seconds)
    {
        seconds = double.IsFinite(seconds) ? Math.Clamp(seconds, -120, 120) : 0;
        switch (Active(engine))
        {
            case VlcPlayerEngine vlc: vlc.Player.SetSpuDelay((long)(seconds * 1_000_000)); break;
            case MpvPlayerEngine mpv: mpv.Command("set", "sub-delay", seconds.ToString(CultureInfo.InvariantCulture)); break;
        }
    }

    public static void AddFile(IPlayerEngine engine, string path)
    {
        path = Path.GetFullPath(path);
        if (!new[] { ".srt", ".ass", ".ssa", ".vtt" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || !File.Exists(path))
            throw new IOException("Выберите существующий файл SRT, ASS, SSA или VTT.");
        switch (Active(engine))
        {
            case VlcPlayerEngine vlc:
                if (!vlc.Player.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true))
                    throw new IOException("Не удалось подключить субтитры.");
                break;
            case MpvPlayerEngine mpv: mpv.Command("sub-add", path, "cached"); break;
            default: throw new IOException("Этот проигрыватель не поддерживает отдельные субтитры.");
        }
    }
}
